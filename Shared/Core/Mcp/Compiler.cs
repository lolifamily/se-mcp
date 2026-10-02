using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
#if NETCOREAPP
using System.Runtime.Loader;
#endif
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shared.Plugin;

namespace Shared.Mcp;

public sealed class CompilationResult
{
    public bool Success { get; }
    private Assembly Assembly { get; }
    public string ErrorOutput { get; }
    // The id baked into this assembly's timeout checks (see InjectGuards) — what the
    // executor publishes while stepping it, so the watchdog can aim a kill at this script alone.
    // A lane with no watchdog compiles no timeout checks; there it only numbers __REPL__N.
    public int ScriptId { get; }

    public CompilationResult(Assembly assembly, int scriptId)
    {
        Success = true;
        Assembly = assembly;
        ScriptId = scriptId;
    }

    public CompilationResult(string errorOutput)
    {
        Success = false;
        ErrorOutput = errorOutput;
    }

    // A frame lane's entry: a fresh instance's Run(), not stepped yet.
    internal IEnumerable<object> Start(Capture output)
    {
        var run = (Func<IEnumerable<object>>)Delegate.CreateDelegate(
            typeof(Func<IEnumerable<object>>), Instantiate(output), "Run");
        return run();
    }

    // The parallel lane's entry (Compiler's asyncEntry): a fresh instance's __Drive, not called yet.
    // Calling it runs the script up to its first await.
    internal Func<Task> StartAsync(Capture output) =>
        (Func<Task>)Delegate.CreateDelegate(typeof(Func<Task>), Instantiate(output), "__Drive");

    // The script's Console holder is filled first, and filling it runs nothing (see
    // Compiler.ClassPrefix), so everything the script writes lands in `output` — class_body's static
    // and instance initializers included. Constructing the instance does run the script's own code,
    // those initializers, so both entries are taken on the thread the script is to run on.
    private object Instantiate(Capture output)
    {
        var type = Assembly.GetType("__REPL__", throwOnError: true)!;
        type.GetNestedType("__Out", BindingFlags.NonPublic)!.GetField("W")!.SetValue(null, output);
        return Activator.CreateInstance(type)!;
    }
}

public sealed class Compiler(MethodInfo guardBail, MethodInfo guardStackCheck, MethodInfo guardKilling, string defaultUsings, bool asyncEntry)
{
    // Numbers both the __REPL__N assembly and its script id, so the two can never disagree about
    // which script it was. Process-wide across both lanes; 1-based, leaving 0 as KillId's "nobody".
    private static int _counter;

    // Roslyn reflection cache — process-constant, populated by the static ctor.
    // The same tokens for both executors; resolving them once instead of per-Compiler
    // saves a redundant LoadRoslyn + dozens of reflection lookups at startup.
    private static readonly MethodInfo ParseText;
    private static readonly MethodInfo CompilationCreate;
    private static readonly object ParseOptions;
    private static readonly object CompileOptions;
    private static readonly Type SyntaxTreeBase;
    private static readonly Type MetaRefBase;
    private static readonly Type ModuleMetadata;
    private static readonly MethodInfo ModuleFromStream;
    private static readonly object MetadataOnly;
    private static readonly MethodInfo ModuleNames;
    private static readonly MethodInfo AssemblyCreate;
    private static readonly MethodInfo AssemblyGetReference;
    private static readonly MethodInfo WithAliases;
    private static readonly MethodInfo Emit;
    private static readonly PropertyInfo EmitSuccess;
    private static readonly PropertyInfo EmitDiags;
    private static readonly PropertyInfo DiagSeverity;

    // References + resolveMap + handler + extern aliases are process-wide: the AppDomain
    // assembly set is identical for both executors, so duplicating the scan + per-file
    // MetadataReference creation gives nothing back. Lifecycle is
    // owned by the plugin (its main-lane pump lazily inits on the first call;
    // Dispose releases). No lock: the pump and Dispose, both on the main
    // thread, are the only writers. The
    // memory-visibility chain to Compile (Task pool) goes through Executor's
    // volatile Initialized flag, which is set AFTER InitShared returns.
    private static bool _sharedInit;
    private static readonly List<object> SharedReferences = [];
    private static readonly Dictionary<string, Assembly> SharedResolveMap = new();
    private static ResolveEventHandler _sharedHandler;
    // `extern alias X;` for every aliased reference (ScriptReferences.Scope). The aliases are the
    // template's, so Compile declares them all ahead of the default usings in every script.
    private static string _externAliases = "";

    // Per-instance tokens — declared as primary constructor parameters above.
    // ScriptGuard{Main,Render}'s Bail/StackCheck/Killing and the entry's shape are
    // what differ between Compiler instances. guardBail and guardKilling are null
    // together for a lane with no watchdog: StackCheck only (see InjectGuards).
    // asyncEntry is the parallel lane's: Run becomes an async iterator (AsyncRunPrefix).

    // Console is the script's output wherever its code runs: code, class_body, nested types, static
    // members, any thread. It reads __Out.W at each call — a holder CompilationResult.Start fills
    // before anything else, with no type initializer of its own, so filling it runs none of the
    // script's code. __REPL__ can't hold it: class_body can give __REPL__ a type initializer, and
    // filling a static field runs its type's initializer first — the script's static initializers
    // would print before their Console existed. `= null` is only there to keep CS0649 (never
    // assigned) out of every failed compile's report; a default value emits no initializer.
    private const string ClassPrefix = """
public class __REPL__
{
    static class __Out { public static global::System.IO.TextWriter W = null; }
    static global::System.IO.TextWriter Console => __Out.W;

""";

    private const string RunPrefix = """
    public IEnumerable<object> Run()
    {

""";

    // The parallel lane's entry (asyncEntry). Run is an async iterator, so `await` compiles there beside
    // `yield return`. __Drive iterates it to its end and is what the lane calls
    // (CompilationResult.StartAsync); it lives in the script, so the iteration's own awaits are script
    // code, built like every other (RetargetBuilders). It goes ahead of Run, so both entries end with the
    // same ClassSuffix.
    private const string AsyncRunPrefix = """
    public async global::System.Threading.Tasks.Task __Drive()
    {
        await foreach (var _ in Run()) { }
    }

    public async IAsyncEnumerable<object> Run()
    {

""";

    private const string ClassSuffix = """
        yield break;
    }
}
""";

    // The IgnoresAccessChecksToAttribute definition — its own compilation unit (own
    // `using System;`, reads naturally). We DECLARE our own instead of `using` an
    // existing one: MULTIPLE loaded assemblies ship this type (0Harmony AND third-party
    // plugins like HdrRender), so `using` it is CS0433-ambiguous. A source-declared type
    // wins over ANY number of imported same-name types (CS0436 warning, filtered out in
    // Compile) — which is exactly why Roslyn/Orleans/etc. declare their own.
    private const string IgnoresAccessAttrDef = """
using System;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) { }
    }
}
""";

    // Two ignoreaccess trees (definition + [assembly:] usage), parsed once by InitShared
    // and compiled alongside every user script.
    private static object _iaAttrTree;
    private static object _iaAssemblyTree;

    // Path of every syntax tree we parse. Positions outside the user's fields (the default
    // usings, the two ignoreaccess trees) keep it, so a diagnostic there prints as
    // "(internal)(line,col)" instead of with no origin at all. See Compile for the #line
    // markers that name the user's own fields.
    private const string InternalPath = "(internal)";

    static Compiler()
    {
        var (csharpAsm, commonAsm) = LoadRoslyn();

        MetaRefBase = commonAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
        SyntaxTreeBase = commonAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree");
        var assemblyMetadata = commonAsm.GetType("Microsoft.CodeAnalysis.AssemblyMetadata", throwOnError: true)!;
        var outputKindType = commonAsm.GetType("Microsoft.CodeAnalysis.OutputKind");
        var docModeType = commonAsm.GetType("Microsoft.CodeAnalysis.DocumentationMode");
        var srcKindType = commonAsm.GetType("Microsoft.CodeAnalysis.SourceCodeKind");
        var compilationType = commonAsm.GetType("Microsoft.CodeAnalysis.Compilation");
        var emitResultType = commonAsm.GetType("Microsoft.CodeAnalysis.Emit.EmitResult");

        var syntaxTreeCsharpType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
        var compilationCsharpType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
        var langVerType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion");
        var parseOptsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
        var compOptsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");

        // What a reference is built with (see Reference). CreateFromStream(Stream, PEStreamOptions)
        // is found by its shape: PEStreamOptions has to be the one from the
        // System.Reflection.Metadata this Roslyn binds to, which need not be ours.
        ModuleMetadata = commonAsm.GetType("Microsoft.CodeAnalysis.ModuleMetadata", throwOnError: true)!;
        ModuleFromStream = ModuleMetadata.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "CreateFromStream"
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.IsEnum);
        MetadataOnly = Enum.Parse(ModuleFromStream.GetParameters()[1].ParameterType, "PrefetchMetadata, LeaveOpen");
        ModuleNames = ModuleMetadata.GetMethod("GetModuleNames", Type.EmptyTypes)
            ?? throw new MissingMethodException(ModuleMetadata.FullName, "GetModuleNames");
        AssemblyCreate = assemblyMetadata.GetMethod("Create", [ModuleMetadata.MakeArrayType()])
            ?? throw new MissingMethodException(assemblyMetadata.FullName, "Create");
        AssemblyGetReference = assemblyMetadata.GetMethod("GetReference")
            ?? throw new MissingMethodException(assemblyMetadata.FullName, "GetReference");

        // For an assembly that would change what a name means in a script (ScriptReferences.Scope).
        // The IEnumerable<string> overload: a string[] crosses into either Roslyn as is, where an
        // ImmutableArray would have to come from the System.Collections.Immutable Roslyn binds to.
        WithAliases = MetaRefBase.GetMethod("WithAliases", [typeof(IEnumerable<string>)])
            ?? throw new MissingMethodException(MetaRefBase.FullName, "WithAliases");

        ParseText = syntaxTreeCsharpType.GetMethod("ParseText",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(string), parseOptsType, typeof(string),
             typeof(System.Text.Encoding), typeof(CancellationToken)], null);

        CompilationCreate = compilationCsharpType.GetMethod("Create",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(string), typeof(IEnumerable<>).MakeGenericType(SyntaxTreeBase),
             typeof(IEnumerable<>).MakeGenericType(MetaRefBase), compOptsType], null);

        ParseOptions = NewWithDefaults(
            parseOptsType.GetConstructor(
                [langVerType, docModeType, srcKindType, typeof(IEnumerable<string>)]),
            langVerType.GetField("Latest").GetValue(null));

        var baseOptions = NewWithDefaults(
            compOptsType.GetConstructors()
                .Single(c => c.GetParameters()[0].ParameterType == outputKindType
                    && c.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never
                    && c.GetParameters().Skip(1).All(p => p.HasDefaultValue)),
            outputKindType.GetField("DynamicallyLinkedLibrary").GetValue(null));

        // allowUnsafe via the With API rather than a ctor argument: the ctor's
        // optional-parameter list shifts across Roslyn versions, while
        // WithAllowUnsafe(bool) is the same single overload on both load paths
        // (game 2.9 and NuGet 5.3).
        var withAllowUnsafe = compOptsType.GetMethod("WithAllowUnsafe", [typeof(bool)])
            ?? throw new MissingMethodException(compOptsType.FullName, "WithAllowUnsafe");
        var unsafeOptions = withAllowUnsafe.Invoke(baseOptions, [true]);

        // ignoreaccess (compile-time half): let REPL scripts read the game's internal
        // types/members. Verified end-to-end on Roslyn 5.3.0. Paired with the runtime
        // [assembly: IgnoresAccessChecksTo] tree built in InitShared.
        //   (1) MetadataImportOptions.Internal — import internal members from metadata
        //       (default Public hides them). WithMetadataImportOptions is PUBLIC.
        //   (2) TopLevelBinderFlags = BinderFlags.IgnoreAccessibility (1<<22) — skip
        //       the CS0122 accessibility check. WithTopLevelBinderFlags is INTERNAL
        //       (NonPublic lookup) — fragile if Roslyn renames it, pinned to 5.3.0.
        var mioType = commonAsm.GetType("Microsoft.CodeAnalysis.MetadataImportOptions", throwOnError: true)!;
        var withMetadataImport = compOptsType.GetMethod("WithMetadataImportOptions",
            BindingFlags.Public | BindingFlags.Instance, null, [mioType], null)
            ?? throw new MissingMethodException(compOptsType.FullName, "WithMetadataImportOptions");
        var internalImport = withMetadataImport.Invoke(unsafeOptions, [Enum.Parse(mioType, "Internal")]);

        var binderFlagsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags", throwOnError: true)!;
        var withTopLevelBinderFlags = compOptsType.GetMethod("WithTopLevelBinderFlags",
            BindingFlags.NonPublic | BindingFlags.Instance, null, [binderFlagsType], null)
            ?? throw new MissingMethodException(compOptsType.FullName, "WithTopLevelBinderFlags");
        CompileOptions = withTopLevelBinderFlags.Invoke(internalImport, [Enum.Parse(binderFlagsType, "IgnoreAccessibility")]);

        Emit = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "Emit"
                && m.GetParameters()[0].ParameterType == typeof(Stream)
                && m.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never
                && m.GetParameters().Skip(1).All(p => p.HasDefaultValue));

        EmitSuccess = emitResultType.GetProperty("Success");
        EmitDiags = emitResultType.GetProperty("Diagnostics");
        DiagSeverity = commonAsm.GetType("Microsoft.CodeAnalysis.Diagnostic", throwOnError: true)!.GetProperty("Severity");
    }

    // Collects what every script compiles against (ScriptReferences.Collect). Deferred until the
    // main lane's first pump so all plugin assemblies are loaded.
    //
    // gameDir is the game's own folder, the one the loader's game-directory AssemblyResolve handler
    // probes: Pulsar and Magnetar point MyFileSystem.ExePath (SE1, DS) and AppContext.BaseDirectory
    // (SE2) at it, from the same value they hand that handler.
    public static void InitShared(string gameDir)
    {
        if (_sharedInit) return;
        _sharedInit = true;

        var entries = ScriptReferences.Collect(gameDir);
        var aliases = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                var reference = Reference(entry.Path);
                if (entry.Alias != null)
                {
                    reference = WithAliases.Invoke(reference, [new[] { entry.Alias }]);
                    aliases.Add(entry.Alias);
                }
                SharedReferences.Add(reference);
                if (entry.LoadFile != null) SharedResolveMap[entry.Name] = entry.LoadFile;
            }
            catch (Exception ex) { Common.Logger.Info($"failed reference {entry.Name}: {ex.Message}"); }
        }
        // Only aliases whose reference made it in: declaring one with none behind it is CS0430 in
        // every script. The '@' lets an alias that happens to spell a C# keyword still be declared.
        _externAliases = string.Concat(aliases.Select(a => $"extern alias @{a};\n"));

        // A plugin loaded with LoadFile can't be found by name, not even by the binder, so its loaded
        // instance is handed back when a script's reference asks for it.
        _sharedHandler = (_, args) =>
        {
            if (args.RequestingAssembly?.GetName().Name?.StartsWith("__REPL__", StringComparison.Ordinal) != true)
                return null;
            return new AssemblyName(args.Name).Name is { } n && SharedResolveMap.TryGetValue(n, out var found)
                ? found
                : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += _sharedHandler;

        // ignoreaccess (runtime half): one [assembly: IgnoresAccessChecksTo(name)] for
        // EVERY referenced assembly — loaded or not yet, plain or aliased — matching
        // the SharedReferences set exactly. (Compile-time
        // internal import is global anyway, so per-assembly runtime gating would only
        // cause "compiles but MethodAccessException at run".) The [assembly:] usages bind
        // to OUR source-declared attribute (wins over the Harmony/other-plugin copies),
        // so no CS0433 no matter how many third-party plugins also declare it.
        _iaAttrTree = CallWithDefaults(ParseText, null, IgnoresAccessAttrDef, ParseOptions, InternalPath);
        // #pragma is lexically scoped to THIS tree only. This tree contains nothing but
        // [assembly: IgnoresAccessChecksTo] lines, whose only CS0436 is our attribute vs
        // the Harmony/other-plugin copies (source wins, harmless) — so this suppresses
        // exactly that one, while the user's own script tree still reports its CS0436s.
        var iaAssembly = "using System.Runtime.CompilerServices;\n"
            + "#pragma warning disable CS0436\n"
            + string.Concat(entries.Select(e => e.Name).Distinct()
                .Select(n => $"[assembly: IgnoresAccessChecksTo(\"{n}\")]\n"));
        _iaAssemblyTree = CallWithDefaults(ParseText, null, iaAssembly, ParseOptions, InternalPath);

        Common.Logger.Info($"{SharedReferences.Count} references collected ({SharedResolveMap.Count} LoadFile)");
    }

    public static void ReleaseShared()
    {
        if (!_sharedInit) return;
        if (_sharedHandler != null)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= _sharedHandler;
            _sharedHandler = null;
        }
        SharedReferences.Clear();
        SharedResolveMap.Clear();
        _externAliases = "";
        _sharedInit = false;
    }

    // One reference: its assembly's metadata, copied into memory, and nothing else of the file.
    // MetadataReference.CreateFromFile keeps the whole file there, IL, resources and precompiled
    // code included: 462 MB of references in SE2, where the metadata is 106 MB and compiling reads
    // nothing else. A file is closed as soon as it's read, so none stays locked.
    //
    // An assembly can be several files, the first naming the others, its modules
    // (System.EnterpriseServices is, of the .NET Framework's). Each is read the same way.
    private static object Reference(string path)
    {
        var manifest = ReadModule(path);
        var names = ((IEnumerable)ModuleNames.Invoke(manifest, null)!).Cast<string>().ToList();
        var modules = Array.CreateInstance(ModuleMetadata, 1 + names.Count);
        modules.SetValue(manifest, 0);
        for (var i = 0; i < names.Count; i++)
            modules.SetValue(ReadModule(Path.Combine(Path.GetDirectoryName(path)!, names[i])), i + 1);

        // GetReference(documentation, aliases, embedInteropTypes, filePath, display): the path is
        // only the reference's label, in a diagnostic that names it.
        return CallWithDefaults(AssemblyGetReference, AssemblyCreate.Invoke(null, [modules]), null, null, false, path);
    }

    private static object ReadModule(string path)
    {
        using var file = File.OpenRead(path);
        return ModuleFromStream.Invoke(null, [file, MetadataOnly]);
    }

    // Three-segment input maps 1:1 to C# language layers:
    //   usings    → compilation-unit-level `using X;` directives
    //   classBody → members of the wrapper class (methods, fields, nested types,
    //               [DllImport] P/Invoke — anything that can't go in a method body)
    //   code      → statements inside the entry method's body
    // McpServer validated that `code` is present; `classBody` and `usings` may be null.
    //
    // Diagnostics name their field with no arithmetic on our side: each segment opens
    // with `#line 1 "<field>"`, so Roslyn maps every position to that field's own line
    // numbers and Diagnostic.ToString() already reads `code(2,9): error CS0103: ...`.
    // A mapping runs until the next #line, so an error landing on the wrapper lines
    // after a segment (an unclosed brace, say) is reported on that segment's trailing
    // lines — the field to fix. Positions before the first marker (the extern aliases
    // and the default usings) keep the tree path, InternalPath.
    public CompilationResult Compile(IReadOnlyList<string> usings, string classBody, string code)
    {
        var usingsBlock = usings == null ? "" : string.Concat(
            usings.Where(u => !string.IsNullOrWhiteSpace(u))
                  .Select(u => "using " + u.Trim() + ";\n"));

        var fullSource = _externAliases + defaultUsings
            + Segment("usings", usingsBlock) + ClassPrefix
            + Segment("class_body", classBody) + (asyncEntry ? AsyncRunPrefix : RunPrefix)
            + Segment("code", code) + ClassSuffix;

        var scriptId = Interlocked.Increment(ref _counter);
        var assemblyName = "__REPL__" + scriptId;

        var tree = CallWithDefaults(ParseText, null, fullSource, ParseOptions, InternalPath);

        // Three files: user script + ignoreaccess [assembly:] usages + our attribute
        // definition. InitShared always runs before any Compile (McpServer gates on
        // Initialized), so both ia trees are set.
        var treesArr = Array.CreateInstance(SyntaxTreeBase, 3);
        treesArr.SetValue(tree, 0);
        treesArr.SetValue(_iaAssemblyTree, 1);
        treesArr.SetValue(_iaAttrTree, 2);

        var refsArr = Array.CreateInstance(MetaRefBase, SharedReferences.Count);
        for (var i = 0; i < SharedReferences.Count; i++)
            refsArr.SetValue(SharedReferences[i], i);

        var compilation = CompilationCreate.Invoke(null, [assemblyName, treesArr, refsArr, CompileOptions]);

        using var ms = new MemoryStream();
        var emitResult = CallWithDefaults(Emit, compilation, ms);

        // Diagnostic.ToString() prints the #line-mapped position — see the comment on Compile.
        if (!(bool)EmitSuccess.GetValue(emitResult)!)
            return new CompilationResult(FailureReport((IEnumerable)EmitDiags.GetValue(emitResult)!));

        ms.Seek(0, SeekOrigin.Begin);
        var raw = ms.ToArray();
        raw = InjectGuards(raw, scriptId);
        return new CompilationResult(Assembly.Load(raw), scriptId);
    }

    // Diagnostics shown in full; the rest are left to one count line.
    private const int MaxDiagnostics = 8;

    // A failed compile's report, as the Minecraft MCP renders one: warnings and errors (Roslyn's
    // DiagnosticSeverity: Hidden 0, Info 1, Warning 2, Error 3), errors first so a run of warnings
    // can't bury the one that failed the compile, then capped. The sort is stable, so within one
    // severity Roslyn's own order survives. DiagnosticSeverity is an int enum, and a boxed enum
    // unboxes to its underlying type.
    private static string FailureReport(IEnumerable diagnostics)
    {
        var shown = diagnostics.Cast<object>()
            .Select(d => (Text: d.ToString(), Severity: (int)DiagSeverity.GetValue(d)!))
            .Where(d => d.Severity >= 2)
            .OrderByDescending(d => d.Severity)
            .ToList();
        var report = string.Join("\n", shown.Take(MaxDiagnostics).Select(d => d.Text));
        return shown.Count > MaxDiagnostics
            ? report + $"\n... {shown.Count - MaxDiagnostics} more diagnostic(s) not shown"
            : report;
    }

    // One user segment, fenced by newlines on both sides so nothing around it can share a
    // line with the user's text: a #line directive has to start a line, and a trailing
    // `// comment` would otherwise swallow the wrapper line after it — Run's header, or the
    // `yield break` that keeps Run an iterator when the user wrote no yield of their own.
    private static string Segment(string field, string text) => $"\n#line 1 \"{field}\"\n{text}\n";

    // Loads the NuGet Roslyn the plugin manifests declare (5.3.0) rather than the older one
    // every game ships (SE1/DS Bin64 2.9, SE2 4.14).
    //
    // The pin below is the .NET Framework path: there a strong-named reference binds to
    // exactly the requested version — no roll-forward — so it must equal the manifests'
    // version, or the bind fails with FileLoadException and the short-name fallback takes
    // the game's own Roslyn. Keep the pin and the three manifests in step.
    //
    // The same exact-version rule caps Roslyn at 5.3 on .NET Framework. 5.6+ depends on
    // System.Collections.Immutable / System.Reflection.Metadata 10.0.1, whose net462 builds
    // are 10.0.0.1 (NuGet bumps .NET Framework assets on every servicing release) while
    // Roslyn references 10.0.0.0. A plugin has no config to carry the binding redirect an
    // app would get, so the bind falls to the loader's game-dir resolver, which answers by
    // name alone with the game's SCI 1.2.3.0: MissingMethodException on the first compile.
    // 5.0–5.3 depend on the 9.0.0 packages, whose net462 builds are 9.0.0.0: exact matches.
    private static (Assembly csharp, Assembly common) LoadRoslyn()
    {
#if NETCOREAPP
        // .NET Core keeps one version per simple name in a load context, and the default
        // context already has the game's Roslyn: no version pin gets past it (the request is
        // refused, and Pulsar's name-only AssemblyResolve hands back the game's copy). So the
        // NuGet copy gets a context of its own. No separate DLL is needed for that — Roslyn
        // is reached purely by reflection, so only BCL types ever cross the boundary.
        //
        // Why the DLLs sit beside this plugin: Pulsar/Magnetar copy the manifest's NuGet
        // runtime files, culture folders included, into the plugin's Bin folder next to the
        // compiled plugin DLL and LoadFrom it there — DevFolder builds in
        // LocalFolderPlugin.InstallDependencies, GitHub installs in
        // GitHubPlugin.CompileFromSource (PluginCache.BinDirectory). A plugin with nothing
        // beside it (a DLL dropped into Local, or one loaded from bytes with an empty
        // Location) falls through to the paths below.
        var dir = Path.GetDirectoryName(typeof(Compiler).Assembly.Location);
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll")))
        {
            try
            {
                var context = new RoslynLoadContext(dir);
                return (
                    context.LoadFromAssemblyName(new AssemblyName("Microsoft.CodeAnalysis.CSharp")),
                    context.LoadFromAssemblyName(new AssemblyName("Microsoft.CodeAnalysis")));
            }
            catch (Exception ex)
            {
                Common.Logger.Warning($"isolated Roslyn load failed ({ex.GetType().Name}), trying the default context");
            }
        }
#endif
        try
        {
            var csharp = Assembly.Load(MakeAssemblyName("Microsoft.CodeAnalysis.CSharp", 5, 3, 0, 0));
            var common = Assembly.Load(MakeAssemblyName("Microsoft.CodeAnalysis", 5, 3, 0, 0));
            return (csharp, common);
        }
        catch (Exception ex)
        {
            Common.Logger.Warning($"NuGet Roslyn unavailable ({ex.GetType().Name}), using game Roslyn");
        }

        return (
            Assembly.Load("Microsoft.CodeAnalysis.CSharp"),
            Assembly.Load("Microsoft.CodeAnalysis"));
    }

#if NETCOREAPP
    // Serves Microsoft.CodeAnalysis* and their culture-folder satellites from the plugin's
    // folder. Every other name returns null and is shared from the default context — the
    // BCL, System.Collections.Immutable and the rest come from the process's one framework.
    private sealed class RoslynLoadContext(string dir) : AssemblyLoadContext("SeMcp.Roslyn")
    {
        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) != true)
                return null;
            var path = string.IsNullOrEmpty(name.CultureName)
                ? Path.Combine(dir, name.Name + ".dll")
                : Path.Combine(dir, name.CultureName, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
#endif

    private static AssemblyName MakeAssemblyName(string name, int major, int minor, int build, int rev)
    {
        var an = new AssemblyName(name) { Version = new Version(major, minor, build, rev) };
        an.SetPublicKeyToken([0x31, 0xbf, 0x38, 0x56, 0xad, 0x36, 0x4e, 0x35]);
        return an;
    }

    private static object CallWithDefaults(MethodInfo method, object target, params object[] leading) =>
        method.Invoke(target, FillDefaults(method.GetParameters(), leading));

    private static object NewWithDefaults(ConstructorInfo ctor, params object[] leading) =>
        ctor.Invoke(FillDefaults(ctor.GetParameters(), leading));

    private static object[] FillDefaults(ParameterInfo[] parms, object[] leading)
    {
        var args = new object[parms.Length];
        for (var i = 0; i < parms.Length; i++)
        {
            if (i < leading.Length)
                args[i] = leading[i];
            else if (parms[i].HasDefaultValue)
                args[i] = parms[i].DefaultValue;
            else
                args[i] = parms[i].ParameterType.IsValueType
                    ? Activator.CreateInstance(parms[i].ParameterType) : null;
        }
        return args;
    }

    // Guard against accidental infinite loops and runaway recursion that would
    // hang the lane's thread. The checks go into the script's own methods, so
    // they also cover REPL code the game or the BCL calls back into (virtual
    // overrides, delegates handed to LINQ, Harmony patches). Three injection
    // points share ScriptGuard state (KillId raised by the lane's FrameWatchdog
    // when a frame's budget runs out; _stackBase captured per step by the first
    // StackCheck):
    //   - Method entry → StackCheck + Bail. Every turn of a recursion enters a
    //     REPL method, directly or through BCL / game code calling back in, so
    //     sampling SP here catches all of it: the step's first check sets
    //     _stackBase, later ones throw once SP is past the budget. The check
    //     runs one frame deeper than a check before the call would, which the
    //     headroom absorbs (lane threads have 1.5MB stacks, the budget is
    //     700KB). The Bail is the timeout check for recursion, which has no
    //     back-edges, and for callbacks a loop outside the script keeps making
    //     (LINQ over a huge sequence).
    //   - Backward branches → Bail. Catches tight loops with no calls.
    //   - Exception handler entry → catch rewritten into a filter that refuses
    //     while this script is being killed; Bail at finally / fault. See
    //     GuardHandlers.
    // Every timeout check asks Killing with scriptId, baked in as a constant, so
    // it fires only for a kill aimed at THIS script and only on the lane's own
    // thread — never for another script, never in this script's code running on
    // other threads, and never once this script has left the lane (code it
    // leaves behind, e.g. a Harmony patch or event handler, keeps running).
    // Each Bail site is `ldc.i4 scriptId; call Bail(int)` — stack-neutral as a
    // pair, and always inserted adjacent, so no region boundary splits it.
    //
    // Everything that answers to KillId — every Bail site and the catch rewrite —
    // is the timeout half, and a lane with no watchdog (guardBail null) gets none
    // of it: nothing will ever raise KillId for its scripts, and its user catch
    // blocks stay exactly as compiled. StackCheck is never optional: a stack
    // overflow can't be caught in .NET, so it takes the whole process with it.
    //
    // And a script's async methods are built by our builders, not the BCL's
    // (RetargetBuilders): on every lane, a faulted script Task can't take SE2
    // down as an unobserved exception, and on the parallel lane an await resumes
    // on the script's own thread whatever its awaiter does.
    // Not a security boundary — token holders already have full RCE.
    private byte[] InjectGuards(byte[] raw, int scriptId)
    {
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(raw));
        var replType = asm.MainModule.Types.FirstOrDefault(t => t.Name == "__REPL__");
        if (replType == null)
            throw new InvalidOperationException("Compiled assembly missing __REPL__ type");

        // Both null on a lane with no watchdog — see above.
        var bailRef = guardBail == null ? null : asm.MainModule.ImportReference(guardBail);
        var killingRef = guardBail == null ? null : asm.MainModule.ImportReference(guardKilling);
        var stackRef = asm.MainModule.ImportReference(guardStackCheck);

        RetargetBuilders(asm.MainModule);

        var types = new Stack<TypeDefinition>();
        types.Push(replType);
        while (types.Count > 0)
        {
            var type = types.Pop();
            foreach (var nested in type.NestedTypes)
                types.Push(nested);
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                // Widen every short-form branch (br.s/leave.s/brfalse.s, ...) to its long
                // form BEFORE inserting anything. Roslyn packs iterator state machines with
                // short branches whose 1-byte ±127 offset overflows once our guard calls
                // bloat the stream — and Cecil does NOT widen an overflowed short branch on
                // write: it emits a truncated offset that lands mid-instruction →
                // InvalidProgramException at JIT time. Long forms carry a 4-byte offset that
                // cannot overflow. (Mono.Cecil.Rocks.SimplifyMacros would do this, but Pulsar
                // ships Mono.Cecil.dll WITHOUT Mono.Cecil.Rocks.dll, so we hand-roll the only
                // macro that affects offset encoding — branches. See WidenShortBranches.)
                WidenShortBranches(method.Body);

                var il = method.Body.GetILProcessor();

                // Snapshot the original IL before we touch it. The back-edge
                // loop below iterates this snapshot, so it won't fall into the
                // entry checks or the filter blocks we splice into catches.
                var originalInstructions = method.Body.Instructions.ToList();

                // Method entry, ahead of the first instruction: outside any try
                // the body opens there, and a branch back to that instruction
                // lands after the checks, so they run once per call.
                var entry = originalInstructions[0];
                il.InsertBefore(entry, il.Create(OpCodes.Call, stackRef));
                InsertBail(il, entry, bailRef, scriptId);

                if (bailRef != null)
                    GuardHandlers(method.Body, il, bailRef, killingRef, scriptId);

                foreach (var ins in originalInstructions)
                {
                    // Roslyn emits an unreachable br.s self-loop right after every
                    // finally/fault handler as the leave-target placeholder
                    // (dotnet/roslyn#51205). Injecting before it lands physically
                    // inside the handler (HandlerEnd is exclusive) → InvalidProgram.
                    if (method.Body.ExceptionHandlers.Any(eh =>
                            eh.HandlerType is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault
                            && eh.HandlerEnd == ins))
                        continue;

                    // Backward branch: Bail only. Tight loops don't push frames,
                    // SP unchanged.
                    //
                    // Intentional: reading the stale .Offset here is SAFE in this pass.
                    // Cecil fills Offset once at read and never updates it — but this pass
                    // only INSERTS instructions (never removes or reorders), and both ins
                    // and t come from the pre-rewrite snapshot, so their relative order is
                    // preserved and the stale offsets stay monotonic. We only need "does t
                    // come before ins?", which a monotonic snapshot answers correctly even
                    // after SimplifyMacros widened the encodings. If this pass ever starts
                    // removing or reordering instructions, this assumption breaks SILENTLY
                    // (no exception, just a wrong verdict) — switch to comparing
                    // body.Instructions.IndexOf(t) <= IndexOf(ins) at that point.
                    if (ins.Operand is Instruction t && t.Offset <= ins.Offset)
                        InsertBail(il, ins, bailRef, scriptId);
                }
            }
        }

        var output = new MemoryStream();
        asm.Write(output);
        return output.ToArray();
    }

    // Handler entries — the timeout half, so InjectGuards runs this only on a lane
    // with a watchdog:
    //  - Catch: rewrite into a filter handler. Filter runs in pass 1
    //    (stackless virtual unwind); rejecting catches while this
    //    script is being killed costs constant stack regardless of
    //    recursion depth.
    //    Previously tried throw-based Bail and `rethrow` opcode —
    //    both cap at ~100 frames because nested ProcessClrException
    //    routing in pass 1/2 is not actually stackless.
    //  - Finally/Fault: `rethrow` / filter are illegal (no caught
    //    exception in scope). Fall back to Bail. Not on the deep-
    //    recursion attack surface.
    //  - Existing Filter (user wrote `catch when`): skipped. Composing
    //    filters is doable but messy — left as a documented gap,
    //    matching SE's behavior.
    //  - Empty finally (HandlerStart == endfinally): skipped, no body.
    //
    // Filter IL layout (the filter block must physically precede the
    // handler block per CIL III.1.6.1):
    //   .filter {
    //     isinst CatchType    ; entry stack [exc] → [exc-or-null]
    //     brfalse reject      ; null → reject (consumes top)
    //     ldc.i4 scriptId
    //     call Killing        ; being killed, on this thread? → [bool]
    //     brtrue reject       ; yes → reject
    //     ldc.i4.1            ; accept
    //     br endLabel
    //   reject:
    //     ldc.i4.0
    //   endLabel:
    //     endfilter           ; single exit; top-of-stack int32 = result
    //   }
    //   { stloc/pop; user catch body... }
    // The call is fine here: it neither throws nor nests a dispatch, and returns
    // before the filter does, so the stack stays constant. On any other thread
    // Killing says no, so the same catch there keeps catching during a kill.
    private static void GuardHandlers(Mono.Cecil.Cil.MethodBody body, ILProcessor il,
        MethodReference bailRef, MethodReference killingRef, int scriptId)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.HandlerType == ExceptionHandlerType.Filter) continue;
            if (eh.HandlerStart.OpCode == OpCodes.Endfinally) continue;

            if (eh.HandlerType != ExceptionHandlerType.Catch)
            {
                // Finally / Fault: keep Bail.
                var ldId = il.Create(OpCodes.Ldc_I4, scriptId);
                il.InsertAfter(eh.HandlerStart, ldId);
                il.InsertAfter(ldId, il.Create(OpCodes.Call, bailRef));
                continue;
            }

            // Single-endfilter exit. Three paths converge on it with an
            // int32 result (0=reject, 1=accept):
            //   accept  : isinst != null AND !Killing(scriptId)  →  push 1
            //   reject1 : isinst == null (wrong type)            →  push 0
            //   reject2 : isinst != null AND Killing(scriptId)   →  push 0
            var endLabel = il.Create(OpCodes.Endfilter);
            var rejectLabel = il.Create(OpCodes.Ldc_I4_0);
            var filterStart = il.Create(OpCodes.Isinst, eh.CatchType);
            var origHandlerStart = eh.HandlerStart;
            il.InsertBefore(origHandlerStart, filterStart);
            il.InsertBefore(origHandlerStart, il.Create(OpCodes.Brfalse, rejectLabel));
            il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ldc_I4, scriptId));
            il.InsertBefore(origHandlerStart, il.Create(OpCodes.Call, killingRef));
            il.InsertBefore(origHandlerStart, il.Create(OpCodes.Brtrue, rejectLabel)); // being killed
            il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(origHandlerStart, il.Create(OpCodes.Br, endLabel));
            il.InsertBefore(origHandlerStart, rejectLabel);                          // ldc.i4.0
            il.InsertBefore(origHandlerStart, endLabel);                             // endfilter

            // Switch handler type and point FilterStart at the filter entry.
            // HandlerStart is unchanged (still the original Roslyn stloc/pop).
            // CatchType must be cleared (filter handlers don't carry a type).
            eh.HandlerType = ExceptionHandlerType.Filter;
            eh.FilterStart = filterStart;
            eh.CatchType = null;

            // Other handlers' TryEnd/HandlerEnd may have referenced
            // origHandlerStart (try-block end == catch-block start, etc.).
            // Repoint them to filterStart so try/handler regions stay glued
            // together physically (CIL III.1.6.1 adjacency requirement).
            foreach (var any in body.ExceptionHandlers)
            {
                if (any.TryEnd     == origHandlerStart) any.TryEnd     = filterStart;
                if (any.HandlerEnd == origHandlerStart) any.HandlerEnd = filterStart;
            }
        }
    }

    // One Bail site before `at`: `ldc.i4 scriptId; call Bail(int)`. None on a lane with
    // no watchdog (bail null), so InjectGuards' insertion sites read the same for every lane.
    private static void InsertBail(ILProcessor il, Instruction at, MethodReference bail, int scriptId)
    {
        if (bail == null) return;
        il.InsertBefore(at, il.Create(OpCodes.Ldc_I4, scriptId));
        il.InsertBefore(at, il.Create(OpCodes.Call, bail));
    }

    // Each BCL builder by full name, and the script builder that takes its place. The ValueTask ones become
    // the Task ones (ValueTasksOnTasks).
    private static readonly Dictionary<string, Type> ScriptBuilders = new()
    {
        ["System.Runtime.CompilerServices.AsyncVoidMethodBuilder"] = typeof(ScriptVoidBuilder),
        ["System.Runtime.CompilerServices.AsyncTaskMethodBuilder"] = typeof(ScriptTaskBuilder),
        ["System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"] = typeof(ScriptTaskBuilder<>),
        ["System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"] = typeof(ScriptTaskBuilder),
        ["System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1"] = typeof(ScriptTaskBuilder<>),
        ["System.Runtime.CompilerServices.AsyncIteratorMethodBuilder"] = typeof(ScriptIteratorBuilder)
    };

    // A module refers to each type it uses from elsewhere through one type reference: the state machines'
    // builder fields, the locals and every call to a builder member all go through it. So pointing that
    // one reference at our builder (ScriptBuilders.cs) swaps the builder of every async method in the
    // script, and not one instruction changes. The runtime then binds each call by name and signature, on
    // our type, which mirrors the BCL's member for member. By full name: on .NET Framework the six live in
    // three assemblies, and in SE2 every game assembly ships one more AsyncVoidMethodBuilder of its own,
    // with the same members.
    private static void RetargetBuilders(ModuleDefinition module)
    {
        // While the ValueTask builders still go by their own names.
        ValueTasksOnTasks(module);

        foreach (var reference in module.GetTypeReferences())
        {
            if (!ScriptBuilders.TryGetValue(reference.FullName, out var ours)) continue;
            var target = module.ImportReference(ours);
            reference.Scope = target.Scope;
            reference.Namespace = target.Namespace;
            reference.Name = target.Name;
        }
    }

    // An async ValueTask method runs on the Task builder. Of the ValueTask builder's members only get_Task
    // names ValueTask, and it is called once, from the method's stub; that call becomes the Task builder's,
    // and the ValueTask is made from its Task right after:
    //     call    ValueTask AsyncValueTaskMethodBuilder::get_Task()
    // becomes
    //     call    Task AsyncValueTaskMethodBuilder::get_Task()     (the type reference is retargeted next)
    //     newobj  ValueTask::.ctor(Task)
    // That ValueTask is the script's own type reference, so nothing in the plugin names one — and nothing
    // could: on .NET Framework the plugin's ValueTask is another type than the scripts', as Pulsar loads
    // Roslyn's NuGet copy of System.Threading.Tasks.Extensions beside the plugin, next to the game's.
    private static void ValueTasksOnTasks(ModuleDefinition module)
    {
        var task = module.ImportReference(typeof(Task));
        var taskOfT = module.ImportReference(typeof(Task<>));
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            var il = method.Body.GetILProcessor();
            foreach (var ins in method.Body.Instructions.ToList())
            {
                if (ins.OpCode.Code != Code.Call || ins.Operand is not MethodReference { Name: "get_Task" } call
                    || call.DeclaringType.GetElementType().FullName is not
                        ("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"
                        or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1"))
                    continue;

                var valueTask = call.ReturnType.GetElementType();
                valueTask.IsValueType = true;
                TypeReference returned = task, ctorOwner = valueTask, ctorParameter = task;
                if (call.DeclaringType is GenericInstanceType bound)
                {
                    returned = Instance(taskOfT, FirstParameterOf(bound.ElementType));   // Task<!0>, the builder's !0
                    ctorOwner = Instance(valueTask, bound.GenericArguments[0]);          // ValueTask<T>
                    ctorParameter = Instance(taskOfT, FirstParameterOf(valueTask));      // Task<!0>, ValueTask`1's !0
                }
                var ctor = new MethodReference(".ctor", module.TypeSystem.Void, ctorOwner) { HasThis = true };
                ctor.Parameters.Add(new ParameterDefinition(ctorParameter));

                ins.Operand = new MethodReference("get_Task", returned, call.DeclaringType) { HasThis = true };
                il.InsertAfter(ins, il.Create(OpCodes.Newobj, ctor));
            }
        }
    }

    private static GenericInstanceType Instance(TypeReference open, TypeReference argument)
    {
        var instance = new GenericInstanceType(open);
        instance.GenericArguments.Add(argument);
        return instance;
    }

    // A generic type's first type parameter, as a signature refers to it: !0.
    private static GenericParameter FirstParameterOf(TypeReference open)
    {
        if (open.GenericParameters.Count == 0)
            open.GenericParameters.Add(new GenericParameter(open));
        return open.GenericParameters[0];
    }

    // Short-form branch opcode → long-form. Mono.Cecil.Rocks.SimplifyMacros would do this
    // (and widen all the other macros too), but Pulsar ships Mono.Cecil.dll WITHOUT
    // Mono.Cecil.Rocks.dll — so we widen by hand the only macros whose encoding length
    // depends on offset distance: branches. Long forms use a 4-byte offset that cannot
    // overflow no matter how much injection bloats the method, which is the entire point.
    // The other macros (ldarg.0, ldc.i4.0, ...) are fixed-length and irrelevant here, so we
    // leave them untouched. We also skip the OptimizeMacros() re-pack: long-form IL is fully
    // valid, the script assembly is single-use, and a few extra bytes per branch is nothing
    // (the form is erased at JIT time anyway — RyuJIT picks its own native jump width).
    private static readonly Dictionary<Code, OpCode> ShortBranchToLong = new()
    {
        { Code.Br_S, OpCodes.Br },
        { Code.Brfalse_S, OpCodes.Brfalse },
        { Code.Brtrue_S, OpCodes.Brtrue },
        { Code.Beq_S, OpCodes.Beq },
        { Code.Bge_S, OpCodes.Bge },
        { Code.Bgt_S, OpCodes.Bgt },
        { Code.Ble_S, OpCodes.Ble },
        { Code.Blt_S, OpCodes.Blt },
        { Code.Bne_Un_S, OpCodes.Bne_Un },
        { Code.Bge_Un_S, OpCodes.Bge_Un },
        { Code.Bgt_Un_S, OpCodes.Bgt_Un },
        { Code.Ble_Un_S, OpCodes.Ble_Un },
        { Code.Blt_Un_S, OpCodes.Blt_Un },
        { Code.Leave_S, OpCodes.Leave }
    };

    private static void WidenShortBranches(Mono.Cecil.Cil.MethodBody body)
    {
        // A branch's Operand is an Instruction reference, untouched by the opcode swap —
        // Cecil re-encodes the (now 4-byte) offset from that reference at write time.
        // Swapping OpCode mutates a struct field in place; it doesn't add/remove
        // instructions, so iterating Instructions while assigning is safe.
        foreach (var ins in body.Instructions)
            if (ShortBranchToLong.TryGetValue(ins.OpCode.Code, out var lng))
                ins.OpCode = lng;
    }
}

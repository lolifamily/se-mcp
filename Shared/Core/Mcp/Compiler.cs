using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
#if NETCOREAPP
using System.Runtime.Loader;
#endif
using System.Text;
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
    // The id baked into this assembly's timeout checks (see InjectGuards): what the
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
    // Compiler.ClassPrefix), so everything the script writes lands in `output`, class_body's static
    // and instance initializers included. Constructing the instance does run the script's own code,
    // those initializers, so both entries are taken on the thread the script is to run on.
    private object Instantiate(Capture output)
    {
        var type = Assembly.GetType("__REPL__", throwOnError: true)!;
        type.GetNestedType("__Out", BindingFlags.NonPublic)!.GetField("W")!.SetValue(null, output);
        return Activator.CreateInstance(type)!;
    }
}

// Compiles one lane's scripts: wraps the three fields in a class, compiles it against everything
// the game can load, injects the guards and loads the result.
//
// The guard arguments are the lane's ScriptGuard members, which the injected code reads and calls;
// guardKillId and guardLaneThread are null together on a lane with no watchdog, which gets StackCheck
// only. asyncEntry is the parallel lane's: Run becomes an async iterator (AsyncRunPrefix).
public sealed class Compiler(FieldInfo guardKillId, FieldInfo guardLaneThread, MethodInfo guardStackCheck, string defaultUsings, bool asyncEntry)
{
    // Numbers both the __REPL__N assembly and its script id, so the two can never disagree about
    // which script it was. Process-wide across both lanes; 1-based, leaving 0 as KillId's "nobody".
    private static int _counter;

    // Roslyn, reached by reflection: resolved once, by the static constructor, for every lane.
    private static readonly MethodInfo ParseText;
    private static readonly MethodInfo CompilationCreate;
    private static readonly object ParseOptions;
    private static readonly object CompileOptions;
    private static readonly Type SyntaxTreeBase;
    private static readonly Type MetaRefBase;
    private static readonly Type ModuleMetadata;
    private static readonly MethodInfo ModuleFromStream;
    private static readonly MethodInfo ModuleFromMetadata;
    private static readonly object MetadataOnly;
    private static readonly MethodInfo ModuleNames;
    private static readonly MethodInfo AssemblyCreate;
    private static readonly MethodInfo AssemblyGetReference;
    private static readonly MethodInfo WithAliases;
    private static readonly MethodInfo Emit;
    private static readonly object EmitOptions;
    private static readonly PropertyInfo EmitSuccess;
    private static readonly PropertyInfo EmitDiags;
    private static readonly PropertyInfo DiagSeverity;

    // What every script compiles against, the same for every lane (InitShared). BuildShared fills it
    // on a thread of its own and sets _sharedReady last; the lanes open only on that, so it is the
    // one volatile field. ReleaseShared takes no lock: it runs as the game exits.
    private static bool _sharedInit;
    private static volatile bool _sharedReady;
    private static readonly List<object> SharedReferences = [];
    private static readonly Dictionary<string, Assembly> SharedResolveMap = new();
    private static ResolveEventHandler _sharedHandler;
    // `extern alias X;` for every aliased reference (ScriptReferences.Scope). The aliases are the
    // template's, so Compile declares them all ahead of the default usings in every script.
    private static string _externAliases = "";

    // Console is the script's output wherever its code runs: code, class_body, nested types, static
    // members, any thread. It reads __Out.W on every call, which CompilationResult fills before
    // anything else: __Out has no type initializer, so filling it runs none of the script's code.
    // __REPL__ can't hold it, as setting a static field runs its type's initializer first, and
    // class_body's static initializers would print before Console existed. `= null` emits no
    // initializer; it only keeps CS0649 out of failed compiles.
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

    // IgnoresAccessChecksToAttribute, declared rather than imported: 0Harmony and other plugins each
    // ship one, so naming theirs is CS0433. A type declared in source wins over every imported copy,
    // with a CS0436 warning that the usages' tree turns off (BuildShared).
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

    // The ignoreaccess trees, the attribute and its usages: parsed once by BuildShared, compiled
    // into every script.
    private static object _iaAttrTree;
    private static object _iaAssemblyTree;

    // The path of every tree we parse. Positions outside the user's fields (the default usings, the
    // ignoreaccess trees) keep it, so a diagnostic there reads "(internal)(line,col)" rather than
    // having no origin; Compile's #line markers name the fields. Trees are parsed as UTF-8: one with
    // a path but no encoding can't go into a PDB (CS8055).
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
        ModuleFromMetadata = ModuleMetadata.GetMethod("CreateFromMetadata", [typeof(IntPtr), typeof(int)])
            ?? throw new MissingMethodException(ModuleMetadata.FullName, "CreateFromMetadata");
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

        // WithAllowUnsafe rather than the ctor, whose optional parameters shift between Roslyn
        // versions: the method is the same on both load paths (game 2.9, NuGet 5.3).
        var withAllowUnsafe = compOptsType.GetMethod("WithAllowUnsafe", [typeof(bool)])
            ?? throw new MissingMethodException(compOptsType.FullName, "WithAllowUnsafe");
        var unsafeOptions = withAllowUnsafe.Invoke(baseOptions, [true]);

        // ignoreaccess, compile-time half (BuildShared has the runtime half), checked end to end on
        // Roslyn 5.3.0: import internal members from metadata, which the default leaves out
        // (MetadataImportOptions.Internal), and skip the accessibility check, CS0122
        // (BinderFlags.IgnoreAccessibility). WithTopLevelBinderFlags is internal to Roslyn: a version
        // that renames it fails here.
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

        // Portable PDB: the Windows default needs Mono.Cecil.Pdb, which Pulsar doesn't ship.
        // EmitOptions.Default is internal, hence the all-defaults ctor (format is its 2nd arg).
        var emitOptionsType = commonAsm.GetType("Microsoft.CodeAnalysis.Emit.EmitOptions", throwOnError: true)!;
        var debugFormatType = commonAsm.GetType("Microsoft.CodeAnalysis.Emit.DebugInformationFormat", throwOnError: true)!;
        EmitOptions = NewWithDefaults(
            emitOptionsType.GetConstructors().Single(c => c.GetParameters().All(p => p.HasDefaultValue)),
            false, Enum.Parse(debugFormatType, "PortablePdb"));

        EmitSuccess = emitResultType.GetProperty("Success");
        EmitDiags = emitResultType.GetProperty("Diagnostics");
        DiagSeverity = commonAsm.GetType("Microsoft.CodeAnalysis.Diagnostic", throwOnError: true)!.GetProperty("Severity");
    }

    // Collects what every script compiles against (ScriptReferences.Collect); true once done.
    // Deferred until the main lane's first pump, when the game's assemblies are loaded. What is
    // loaded decides what gets aliased, so it's read here; BuildShared reads the files.
    //
    // gameDir is the game's own folder, the one the loader's game-directory AssemblyResolve handler
    // probes: Pulsar and Magnetar point MyFileSystem.ExePath (SE1, DS) and AppContext.BaseDirectory
    // (SE2) at it, from the same value they hand that handler.
    public static bool InitShared(string gameDir)
    {
        if (_sharedInit) return _sharedReady;
        _sharedInit = true;

        // A plugin loaded with LoadFile can't be found by name, not even by the binder, so its loaded
        // instance is handed back when a script's reference asks for it. Registered here, on
        // ReleaseShared's thread; no script asks before the build is in.
        _sharedHandler = (_, args) =>
        {
            if (args.RequestingAssembly?.GetName().Name?.StartsWith("__REPL__", StringComparison.Ordinal) != true)
                return null;
            return new AssemblyName(args.Name).Name is { } n && SharedResolveMap.TryGetValue(n, out var found)
                ? found
                : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += _sharedHandler;

        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        new Thread(() => BuildShared(gameDir, loaded)) { IsBackground = true, Name = "SeMcp references" }.Start();
        return false;
    }

    // Its own thread: nothing may escape, an unhandled exception there ends the process.
    private static void BuildShared(string gameDir, Assembly[] loaded)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var entries = ScriptReferences.Collect(gameDir, loaded);
            var aliases = new List<string>();
            foreach (var entry in entries)
            {
                try
                {
                    var reference = Reference(entry);
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

            // ignoreaccess, runtime half: an IgnoresAccessChecksTo for every assembly a script
            // compiles against, loaded or not, aliased or not. Compiling sees every internal anyway,
            // so a script using one from an assembly left out would compile and then throw
            // MethodAccessException.
            _iaAttrTree = CallWithDefaults(ParseText, null, IgnoresAccessAttrDef, ParseOptions, InternalPath, Encoding.UTF8);
            // The usages' only CS0436 is our attribute against the Harmony and plugin copies, which
            // ours beats. The #pragma reaches this tree alone, so a script still sees its own.
            var iaAssembly = "using System.Runtime.CompilerServices;\n"
                + "#pragma warning disable CS0436\n"
                + string.Concat(entries.Select(e => e.Name).Distinct()
                    .Select(n => $"[assembly: IgnoresAccessChecksTo(\"{n}\")]\n"));
            _iaAssemblyTree = CallWithDefaults(ParseText, null, iaAssembly, ParseOptions, InternalPath, Encoding.UTF8);

            var ms = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
            Common.Logger.Info($"{SharedReferences.Count} references collected ({SharedResolveMap.Count} LoadFile) in {ms} ms");
            _sharedReady = true;
        }
        catch (Exception ex)
        {
            Common.Logger.Error(ex, "collecting script references failed: execute_code stays unavailable");
        }
    }

    // Plugin.Dispose, as the game exits: unhooks the resolver and drops the references.
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
    // (System.EnterpriseServices is, of the .NET Framework's). Each is read the same way. One with
    // types to hide is read from HiddenTypes' patched copy instead, always of one module.
    private static object Reference(ScriptReferences.Entry entry)
    {
        Array modules;
        if (entry.Hidden.Count > 0 && HiddenTypes.Patch(entry.Path, entry.Hidden) is { } patched)
        {
            modules = Array.CreateInstance(ModuleMetadata, 1);
            modules.SetValue(ModuleFromMetadata.Invoke(null, [patched.Pointer, patched.Size]), 0);
        }
        else
        {
            if (entry.Hidden.Count > 0) Common.Logger.Warning($"can't hide {entry.Hidden.Count} types of {entry.Name}: referenced as is");
            var manifest = ReadModule(entry.Path);
            var names = ((IEnumerable)ModuleNames.Invoke(manifest, null)!).Cast<string>().ToList();
            modules = Array.CreateInstance(ModuleMetadata, 1 + names.Count);
            modules.SetValue(manifest, 0);
            for (var i = 0; i < names.Count; i++)
                modules.SetValue(ReadModule(Path.Combine(Path.GetDirectoryName(entry.Path)!, names[i])), i + 1);
        }

        // GetReference(documentation, aliases, embedInteropTypes, filePath, display): the path is
        // only the reference's label, in a diagnostic that names it.
        return CallWithDefaults(AssemblyGetReference, AssemblyCreate.Invoke(null, [modules]), null, null, false, entry.Path);
    }

    private static object ReadModule(string path)
    {
        using var file = File.OpenRead(path);
        return ModuleFromStream.Invoke(null, [file, MetadataOnly]);
    }

    // Each field goes where C# takes it: usings as using directives, class_body as members of the
    // wrapper class (anything a method body can't hold, [DllImport] included), code as Run's body.
    // McpServer checked that code is there; class_body and usings may be null.
    //
    // Each field opens with `#line 1 "<field>"`, so Roslyn itself gives positions in the field's own
    // lines, with no arithmetic on our side: in diagnostics (`code(2,9): error CS0103: ...`) and,
    // through the PDB, in stack traces (`in code:line 2`). An error on the wrapper after a field (an
    // unclosed brace, say) is put on that field's trailing lines, the field to fix. Positions ahead of
    // the first field, in the extern aliases and the default usings, keep InternalPath.
    //
    // Roslyn checks `cancel` while parsing and throughout Emit. Its OperationCanceledException
    // arrives wrapped in TargetInvocationException, so callers test the token, not the exception.
    public CompilationResult Compile(IReadOnlyList<string> usings, string classBody, string code, CancellationToken cancel)
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

        var tree = CallCancellable(ParseText, null, cancel, fullSource, ParseOptions, InternalPath, Encoding.UTF8);

        // The script and the ignoreaccess trees, set by now: no lane opens before BuildShared is done.
        var treesArr = Array.CreateInstance(SyntaxTreeBase, 3);
        treesArr.SetValue(tree, 0);
        treesArr.SetValue(_iaAssemblyTree, 1);
        treesArr.SetValue(_iaAttrTree, 2);

        var refsArr = Array.CreateInstance(MetaRefBase, SharedReferences.Count);
        for (var i = 0; i < SharedReferences.Count; i++)
            refsArr.SetValue(SharedReferences[i], i);

        var compilation = CompilationCreate.Invoke(null, [assemblyName, treesArr, refsArr, CompileOptions]);

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        // Emit(peStream, pdbStream, xmlDocumentationStream, win32Resources, manifestResources, options, ...)
        var emitResult = CallCancellable(Emit, compilation, cancel, pe, pdb, null, null, null, EmitOptions);

        if (!(bool)EmitSuccess.GetValue(emitResult)!)
            return new CompilationResult(FailureReport((IEnumerable)EmitDiags.GetValue(emitResult)!));

        var (raw, symbols) = InjectGuards(pe.ToArray(), pdb.ToArray(), scriptId);
        // A loaded assembly stays loaded for the life of the process, its PDB with it.
        cancel.ThrowIfCancellationRequested();
        return new CompilationResult(Assembly.Load(raw, symbols), scriptId);
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

    // One field, on lines of its own: a #line directive has to start a line, and a trailing
    // `// comment` would swallow the wrapper line after it (Run's header, or the `yield break` that
    // keeps Run an iterator when the script has no yield). `#line hidden` closes it, so the wrapper's
    // frames carry no line in a stack trace instead of a wrong one in this field; diagnostics on the
    // wrapper still name the field.
    private static string Segment(string field, string text) => $"\n#line 1 \"{field}\"\n{text}\n#line hidden\n";

    // The NuGet Roslyn the plugin manifests declare (5.3.0), not the older one each game ships
    // (SE1/DS 2.9, SE2 4.14).
    //
    // The pin below is for .NET Framework, where a strong-named reference binds to exactly the
    // version asked for: unless it matches the three manifests, the bind fails (FileLoadException)
    // and the game's Roslyn is taken instead. Keep the pin and the manifests in step.
    //
    // The same rule caps Roslyn at 5.3 there. 5.6+ references System.Collections.Immutable and
    // System.Reflection.Metadata 10.0.0.0, but the net462 builds in their 10.0.1 packages are
    // 10.0.0.1 (NuGet bumps .NET Framework assembly versions on every servicing release), and a
    // plugin has no config for a binding redirect: the loader's name-only resolver hands back the
    // game's SCI 1.2.3.0, and the first compile throws MissingMethodException. 5.0–5.3 take the
    // 9.0.0 packages, whose net462 builds are 9.0.0.0, an exact match.
    private static (Assembly csharp, Assembly common) LoadRoslyn()
    {
#if NETCOREAPP
        // A load context holds one version per name, and the default one already has the game's
        // Roslyn: no pin gets past it, as Pulsar's name-only resolver hands back the game's copy.
        // So the NuGet copy gets a context of its own, with no bridge assembly: Roslyn is reached by
        // reflection alone, so only BCL types cross.
        //
        // Pulsar and Magnetar copy the manifest's NuGet files, culture folders included, into the
        // plugin's Bin folder, beside this assembly (DevFolder: LocalFolderPlugin.InstallDependencies;
        // GitHub: GitHubPlugin.CompileFromSource, into PluginCache.BinDirectory). A plugin with nothing
        // beside it (dropped into Local, or loaded from bytes, with no Location) falls through to the
        // paths below.
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
    // folder. Every other name returns null and is shared from the default context: the
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

    // Puts `cancel` in the CancellationToken parameter wherever it is (Emit's is 11th of 11).
    private static object CallCancellable(MethodInfo method, object target, CancellationToken cancel, params object[] leading)
    {
        var parms = method.GetParameters();
        var args = FillDefaults(parms, leading);
        args[Array.FindIndex(parms, p => p.ParameterType == typeof(CancellationToken))] = cancel;
        return method.Invoke(target, args);
    }

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

    // Guards against a script hanging its lane's thread with a runaway loop or recursion. They go
    // into the script's own methods, so they also cover its code the game or the BCL calls back
    // into (overrides, delegates handed to LINQ, Harmony patches):
    //   - Method entry: StackCheck, then the kill test. Every turn of a recursion enters a script
    //     method, so checking the stack there catches all of it: the step's first check takes the
    //     baseline, later ones throw past the budget (ScriptGuard's 700KB, on 1.5MB lane stacks),
    //     whose headroom covers checking a frame later than a check before the call would. The kill
    //     test here is the timeout check for recursion, which has no back-edges, and for callbacks a
    //     loop outside the script keeps making (LINQ over a huge sequence).
    //   - Backward branches: the kill test, for tight loops with no calls.
    //   - Handlers: a catch becomes a filter that refuses while the script is being killed, a
    //     finally or fault gets the kill test (GuardHandlers).
    // The kill test (KillTest) is stack-neutral and inserted in one piece, so no region boundary
    // splits it. The id baked into it makes a kill (FrameWatchdog raising KillId) reach this script
    // alone, on the lane's thread alone: never another script, and never this one's code on other
    // threads or after it left the lane (a Harmony patch, an event handler).
    //
    // A lane with no watchdog (guardKillId null) gets none of the timeout half, and its catches stay
    // as compiled. It still gets StackCheck: a stack overflow can't be caught in .NET, and takes the
    // process down with it.
    //
    // A script's async methods also get our builders (RetargetBuilders): on every lane, a faulted
    // script Task can't take SE2 down as an unobserved exception, and on the parallel lane an await
    // resumes on the script's own thread whatever its awaiter does.
    //
    // Not a security boundary: whoever holds the token can run anything anyway.
    private (byte[] Pe, byte[] Pdb) InjectGuards(byte[] pe, byte[] pdb, int scriptId)
    {
        // The PDB goes through Cecil too: the insertions below shift IL offsets, and Cecil keeps
        // each sequence point on its instruction. Roslyn's PDB would map throws to wrong lines.
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(pe), new ReaderParameters
        {
            ReadSymbols = true,
            SymbolStream = new MemoryStream(pdb),
            SymbolReaderProvider = new PortablePdbReaderProvider()
        });
        var replType = asm.MainModule.Types.FirstOrDefault(t => t.Name == "__REPL__");
        if (replType == null)
            throw new InvalidOperationException("Compiled assembly missing __REPL__ type");

        // Null on a lane with no watchdog (see above).
        var kill = guardKillId == null ? null : new KillTest(asm.MainModule, guardKillId, guardLaneThread, scriptId);
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

                // Before any insertion, which could push a short branch's target out of reach
                // (ShortBranchToLong).
                WidenShortBranches(method.Body);

                var il = method.Body.GetILProcessor();

                // The IL as compiled: the back-edge pass walks this, not the checks spliced in.
                var originalInstructions = method.Body.Instructions.ToList();

                // Ahead of the first instruction, outside any try the body opens there. A branch
                // back to that instruction lands after the checks, so they run once per call.
                var entry = originalInstructions[0];
                il.InsertBefore(entry, il.Create(OpCodes.Call, stackRef));
                InsertKillTest(il, entry, kill);

                if (kill != null)
                    GuardHandlers(method.Body, il, kill);

                foreach (var ins in originalInstructions)
                {
                    // Roslyn puts an unreachable br.s self-loop, a leave target, right after every
                    // finally or fault (dotnet/roslyn#51205). A kill test before it would land inside
                    // the handler, which ends there: InvalidProgramException.
                    if (method.Body.ExceptionHandlers.Any(eh =>
                            eh.HandlerType is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault
                            && eh.HandlerEnd == ins))
                        continue;

                    // Backward branch: the kill test only, as a tight loop pushes no frames. Offsets
                    // are the ones read before any insertion, still in order as this pass only
                    // inserts; a pass that removed or reordered would have to compare indices
                    // instead, or get wrong answers without a word.
                    if (ins.Operand is Instruction t && t.Offset <= ins.Offset)
                        InsertKillTest(il, ins, kill);
                }
            }
        }

        // Rebuild info (compilation options, every reference): ~17 of the PDB's 20 KB, unused by
        // stack traces, and held for the life of the process.
        asm.MainModule.CustomDebugInformations.Clear();

        var peOut = new MemoryStream();
        var pdbOut = new MemoryStream();
        asm.Write(peOut, new WriterParameters
        {
            WriteSymbols = true,
            SymbolStream = pdbOut,
            SymbolWriterProvider = new PortablePdbWriterProvider()
        });
        return (peOut.ToArray(), pdbOut.ToArray());
    }

    // The timeout half for handlers, on a lane with a watchdog only:
    //  - catch: becomes a filter that refuses while this script is being killed. A filter runs in
    //    the first pass, before anything unwinds, so refusing costs no stack at any recursion depth.
    //    A throwing check or a `rethrow` both gave out about 100 frames deep: the runtime's nested
    //    exception dispatch (ProcessClrException, both passes) isn't stackless.
    //  - finally / fault: the kill test after the first instruction, inside the handler, as no
    //    exception is in scope to filter or rethrow. Not what deep recursion runs into.
    //  - a filter already (`catch when`): left alone. Composing two filters is messy, and SE's own
    //    script guard leaves the same gap.
    //  - an empty finally: left alone.
    //
    // The filter, which has to sit right before its handler (CIL III.1.6.1):
    //     isinst   CatchType       ; the exception, or null
    //     brfalse  reject          ; another type
    //     <kill test, alive: accept>
    //     br       reject          ; being killed, on this thread: pass it on
    //   accept:
    //     ldc.i4.1                 ; catch it
    //     br       end
    //   reject:
    //     ldc.i4.0
    //   end:
    //     endfilter
    // The test neither throws nor starts a dispatch, so the stack stays constant; on any other
    // thread it says no, and the same catch there keeps catching during a kill.
    private static void GuardHandlers(Mono.Cecil.Cil.MethodBody body, ILProcessor il, KillTest kill)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.HandlerType == ExceptionHandlerType.Filter) continue;
            if (eh.HandlerStart.OpCode == OpCodes.Endfinally) continue;

            if (eh.HandlerType != ExceptionHandlerType.Catch)
            {
                InsertKillTest(il, eh.HandlerStart.Next, kill);
                continue;
            }

            // The filter above, spliced in ahead of Roslyn's handler start.
            var filterStart = il.Create(OpCodes.Isinst, eh.CatchType);
            var accept = il.Create(OpCodes.Ldc_I4_1);
            var reject = il.Create(OpCodes.Ldc_I4_0);
            var end = il.Create(OpCodes.Endfilter);
            var origHandlerStart = eh.HandlerStart;
            Instruction[] filter =
            [
                filterStart,
                il.Create(OpCodes.Brfalse, reject),
                .. kill.Branch(il, accept),
                il.Create(OpCodes.Br, reject),
                accept,
                il.Create(OpCodes.Br, end),
                reject,
                end
            ];
            foreach (var ins in filter)
                il.InsertBefore(origHandlerStart, ins);

            // HandlerStart stays on Roslyn's stloc/pop; a filter handler carries no CatchType.
            eh.HandlerType = ExceptionHandlerType.Filter;
            eh.FilterStart = filterStart;
            eh.CatchType = null;

            // A try or handler that ended where this handler began now ends at its filter, so the
            // regions stay adjacent (CIL III.1.6.1).
            foreach (var any in body.ExceptionHandlers)
            {
                if (any.TryEnd     == origHandlerStart) any.TryEnd     = filterStart;
                if (any.HandlerEnd == origHandlerStart) any.HandlerEnd = filterStart;
            }
        }
    }

    // A kill test before `at`, throwing when it fires and going on to `at` when it doesn't. None on a
    // lane with no watchdog (kill null), so InjectGuards' insertion sites read the same for every lane.
    private static void InsertKillTest(ILProcessor il, Instruction at, KillTest kill)
    {
        if (kill == null) return;
        foreach (var ins in kill.Throw(il, at))
            il.InsertBefore(at, ins);
    }

    // Is this script being killed, here? KillId first, so outside a kill the test is a load, a compare
    // and a branch, and the thread is only asked once the id matches. Both loads are volatile, as the
    // fields are: a plain one could be hoisted out of a loop, which would then never see the kill.
    private sealed class KillTest(ModuleDefinition module, FieldInfo killId, FieldInfo laneThread, int scriptId)
    {
        private readonly FieldReference killIdRef = module.ImportReference(killId);
        private readonly FieldReference laneThreadRef = module.ImportReference(laneThread);
        private readonly MethodReference threadId =
            module.ImportReference(typeof(Environment).GetProperty(nameof(Environment.CurrentManagedThreadId))!.GetMethod);
        private readonly MethodReference timeout =
            module.ImportReference(typeof(ScriptTimeoutException).GetConstructor(Type.EmptyTypes));

        // Falls through when this script is being killed on this thread; branches to `alive` otherwise.
        public Instruction[] Branch(ILProcessor il, Instruction alive) =>
        [
            il.Create(OpCodes.Volatile),
            il.Create(OpCodes.Ldsfld, killIdRef),
            il.Create(OpCodes.Ldc_I4, scriptId),
            il.Create(OpCodes.Bne_Un, alive),
            il.Create(OpCodes.Call, threadId),
            il.Create(OpCodes.Volatile),
            il.Create(OpCodes.Ldsfld, laneThreadRef),
            il.Create(OpCodes.Bne_Un, alive)
        ];

        public Instruction[] Throw(ILProcessor il, Instruction alive) =>
            [.. Branch(il, alive), il.Create(OpCodes.Newobj, timeout), il.Create(OpCodes.Throw)];
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
    // That ValueTask is the script's own type reference, so nothing in the plugin names one, and nothing
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

    // Each short branch's long form. A short branch has a 1-byte offset, and Roslyn packs iterator
    // state machines with them: the guards' insertions push targets out of reach, and Cecil writes
    // the overflowed offset truncated, an InvalidProgramException at JIT time. The long form's 4
    // bytes can't overflow. Mono.Cecil.Rocks' SimplifyMacros would do this, but Pulsar ships
    // Mono.Cecil without Rocks. Branches are the only macros whose size depends on distance, and
    // long-form IL is just as valid, so nothing is packed back afterwards: the JIT picks its own jump
    // widths anyway.
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
        // A branch's operand is its target instruction, so Cecil works out the 4-byte offset on
        // write. Swapping an opcode in place adds or removes nothing, so the loop can assign as it goes.
        foreach (var ins in body.Instructions)
            if (ShortBranchToLong.TryGetValue(ins.OpCode.Code, out var lng))
                ins.OpCode = lng;
    }
}

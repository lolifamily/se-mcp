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
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shared.Plugin;

namespace Shared.Mcp;

public sealed class CompilationResult
{
    public bool Success { get; }
    public Assembly Assembly { get; }
    public string ErrorOutput { get; }
    // The id baked into this assembly's timeout checks (see InjectTimeoutChecks) — what the
    // executor publishes while stepping it, so the watchdog can aim a kill at this script alone.
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
}

public sealed class Compiler(MethodInfo guardBail, MethodInfo guardStackCheck, FieldInfo guardKillId, string defaultUsings)
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
    private static readonly MethodInfo CreateFromFile;
    private static readonly MethodInfo Emit;
    private static readonly PropertyInfo EmitSuccess;
    private static readonly PropertyInfo EmitDiags;

    // References + resolveMap + handler are process-wide: the AppDomain assembly
    // set is identical for both executors, so duplicating the scan + per-file
    // Mono.Cecil MetadataReference creation gives nothing back. Lifecycle is
    // owned by Plugin (Update lazily inits on first call; Dispose releases).
    // No lock: Plugin's main-thread Update/Dispose are the only writers. The
    // memory-visibility chain to Compile (Task pool) goes through Executor's
    // volatile Initialized flag, which is set AFTER InitShared returns.
    private static bool _sharedInit;
    private static readonly List<object> SharedReferences = [];
    private static readonly Dictionary<string, Assembly> SharedResolveMap = new();
    private static ResolveEventHandler _sharedHandler;

    // Per-instance tokens — declared as primary constructor parameters above.
    // ScriptGuard{Main,Render}'s Bail/StackCheck/KillId are the only thing that
    // differs between the two Compiler instances.

    private const string ClassPrefix = """
public class __REPL__
{

""";

    private const string RunPrefix = """
    public IEnumerable<object> Run(TextWriter Console)
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
        var metaRefPropsType = commonAsm.GetType("Microsoft.CodeAnalysis.MetadataReferenceProperties");
        var docProviderType = commonAsm.GetType("Microsoft.CodeAnalysis.DocumentationProvider");
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

        CreateFromFile = MetaRefBase.GetMethod("CreateFromFile",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(string), metaRefPropsType, docProviderType], null);

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
        // (game 2.9 and NuGet 5.9).
        var withAllowUnsafe = compOptsType.GetMethod("WithAllowUnsafe", [typeof(bool)])
            ?? throw new MissingMethodException(compOptsType.FullName, "WithAllowUnsafe");
        var unsafeOptions = withAllowUnsafe.Invoke(baseOptions, [true]);

        // ignoreaccess (compile-time half): let REPL scripts read the game's internal
        // types/members. Verified end-to-end on Roslyn 5.9.0. Paired with the runtime
        // [assembly: IgnoresAccessChecksTo] tree built in InitShared.
        //   (1) MetadataImportOptions.Internal — import internal members from metadata
        //       (default Public hides them). WithMetadataImportOptions is PUBLIC.
        //   (2) TopLevelBinderFlags = BinderFlags.IgnoreAccessibility (1<<22) — skip
        //       the CS0122 accessibility check. WithTopLevelBinderFlags is INTERNAL
        //       (NonPublic lookup) — fragile if Roslyn renames it, pinned to 5.9.0.
        var mioType = commonAsm.GetType("Microsoft.CodeAnalysis.MetadataImportOptions");
        var withMetadataImport = compOptsType.GetMethod("WithMetadataImportOptions",
            BindingFlags.Public | BindingFlags.Instance, null, [mioType], null)
            ?? throw new MissingMethodException(compOptsType.FullName, "WithMetadataImportOptions");
        var internalImport = withMetadataImport.Invoke(unsafeOptions, [Enum.Parse(mioType, "Internal")]);

        var binderFlagsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
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
    }

    // Re-resolve each unique name via Assembly.Load so CLR picks the version
    // that runtime binding (probing paths + redirects) would actually use.
    // Deferred until first Update() so all plugin assemblies are loaded.
    //
    // Cached Pulsar GitHubPlugins are loaded via Assembly.LoadFile, placing
    // them outside the default Load context. Assembly.Load(name) fails for
    // their randomized names. We collect those into a separate bucket and
    // pick the highest version per name, then register an AssemblyResolve
    // handler so REPL code can find them at runtime.
    public static void InitShared()
    {
        if (_sharedInit) return;
        _sharedInit = true;

        var loadContext = new Dictionary<string, string>();
        var loadFile = new Dictionary<string, (Assembly asm, Version ver)>();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            var name = asm.GetName().Name;
            if (name == null || loadContext.ContainsKey(name)) continue;

            try
            {
                loadContext[name] = Assembly.Load(name).Location;
            }
            catch
            {
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc)) continue;
                var ver = asm.GetName().Version ?? new Version(0, 0);
                if (!loadFile.TryGetValue(name, out var prev) || ver > prev.ver)
                    loadFile[name] = (asm, ver);
            }
        }

        foreach (var (name, loc) in loadContext)
        {
            if (string.IsNullOrEmpty(loc)) continue;
            if (Path.GetFileName(loc) == "VRage.Native.dll"
                || Path.GetFileName(loc).StartsWith("Mono.Cecil", StringComparison.Ordinal)) continue;
            try { SharedReferences.Add(CallWithDefaults(CreateFromFile, null, loc)); }
            catch (Exception ex) { Common.Logger.Info($"failed reference {name}: {ex.Message}"); }
        }

        foreach (var (name, (asm, _)) in loadFile)
        {
            var loc = asm.Location;
            if (Path.GetFileName(loc) == "VRage.Native.dll"
                || Path.GetFileName(loc).StartsWith("Mono.Cecil", StringComparison.Ordinal)) continue;
            try
            {
                SharedReferences.Add(CallWithDefaults(CreateFromFile, null, loc));
                SharedResolveMap[name] = asm;
            }
            catch (Exception ex) { Common.Logger.Info($"failed LoadFile reference {name}: {ex.Message}"); }
        }

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
        // EVERY referenced assembly — game + BCL (loadContext) and other plugins
        // (loadFile) alike, matching the SharedReferences set exactly. (Compile-time
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
            + string.Concat(loadContext.Keys.Concat(loadFile.Keys).Distinct()
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
        _sharedInit = false;
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
    // lines — the field to fix. Positions before the first marker (the default usings)
    // keep the tree path, InternalPath.
    public CompilationResult Compile(IReadOnlyList<string> usings, string classBody, string code)
    {
        var usingsBlock = usings == null ? "" : string.Concat(
            usings.Where(u => !string.IsNullOrWhiteSpace(u))
                  .Select(u => "using " + u.Trim() + ";\n"));

        var fullSource = defaultUsings
            + Segment("usings", usingsBlock) + ClassPrefix
            + Segment("class_body", classBody) + RunPrefix
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
            return new CompilationResult(string.Join("\n", ((IEnumerable)EmitDiags.GetValue(emitResult)!).Cast<object>()));

        ms.Seek(0, SeekOrigin.Begin);
        var raw = ms.ToArray();
        raw = InjectTimeoutChecks(raw, scriptId);
        return new CompilationResult(Assembly.Load(raw), scriptId);
    }

    // One user segment, fenced by newlines on both sides so nothing around it can share a
    // line with the user's text: a #line directive has to start a line, and a trailing
    // `// comment` would otherwise swallow the wrapper line after it — Run's header, or the
    // `yield break` that keeps Run an iterator when the user wrote no yield of their own.
    private static string Segment(string field, string text) => $"\n#line 1 \"{field}\"\n{text}\n";

    // Loads the NuGet Roslyn the plugin manifests declare (5.9.0) rather than the older one
    // every game ships (SE1/DS Bin64 2.9, SE2 4.14).
    //
    // The pin below is the .NET Framework path: there a strong-named reference binds to
    // exactly the requested version — no roll-forward — so it must equal the manifests'
    // version, or the bind fails with FileLoadException and the short-name fallback takes
    // the game's own Roslyn. Keep the pin and the three manifests in step.
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
            var csharp = Assembly.Load(MakeAssemblyName("Microsoft.CodeAnalysis.CSharp", 5, 9, 0, 0));
            var common = Assembly.Load(MakeAssemblyName("Microsoft.CodeAnalysis", 5, 9, 0, 0));
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
    // hang the game thread. Three injection points share ScriptGuard state
    // (KillId raised by the lane's FrameWatchdog when a frame's budget runs
    // out; StackBase captured per-script on first StackCheck). Every timeout
    // check compares KillId against scriptId, baked in as a constant, so it
    // fires only for a kill aimed at THIS script — never another script's,
    // and never once this script has left the lane (code it leaves behind,
    // e.g. a Harmony patch or event handler, keeps running):
    //   - Exception handler entry:
    //       catch → rewritten into a filter handler that rejects while this
    //         script is being killed. The filter runs in CLR's pass 1 (stackless
    //         virtual unwind), so deep-recursion-plus-catch attacks unwind in
    //         constant stack.
    //       finally / fault → Bail (filter is illegal here; no caught exception).
    //   - Backward branches → Bail. Catches tight loops with no method calls.
    //   - REPL / Delegate call sites → StackCheck + Bail. Catches recursion
    //     by sampling SP; first call per script sets StackBase, subsequent
    //     calls throw if SP descends past the budget.
    // Each Bail site is `ldc.i4 scriptId; call Bail(int)` — stack-neutral as a
    // pair, and always inserted adjacent, so no region boundary splits it.
    // Not a security boundary — token holders already have full RCE.
    private byte[] InjectTimeoutChecks(byte[] raw, int scriptId)
    {
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(raw));
        var replType = asm.MainModule.Types.FirstOrDefault(t => t.Name == "__REPL__");
        if (replType == null)
            throw new InvalidOperationException("Compiled assembly missing __REPL__ type");

        var bailRef = asm.MainModule.ImportReference(guardBail);
        var killIdRef = asm.MainModule.ImportReference(guardKillId);
        var stackRef = asm.MainModule.ImportReference(guardStackCheck);

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

                // Snapshot the original IL before we touch handlers. The
                // backward-branch / call-site loop below iterates this snapshot,
                // so it won't fall into the filter blocks we splice into catches.
                var originalInstructions = method.Body.Instructions.ToList();

                // Handler entries:
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
                //     ldsfld KillId
                //     ldc.i4 scriptId
                //     beq reject          ; this script is being killed → reject
                //     ldc.i4.1            ; accept
                //     br endLabel
                //   reject:
                //     ldc.i4.0
                //   endLabel:
                //     endfilter           ; single exit; top-of-stack int32 = result
                //   }
                //   { stloc/pop; user catch body... }
                foreach (var eh in method.Body.ExceptionHandlers)
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
                    //   accept  : isinst != null AND KillId != scriptId  →  push 1
                    //   reject1 : isinst == null (wrong type)            →  push 0
                    //   reject2 : isinst != null AND KillId == scriptId  →  push 0
                    var endLabel = il.Create(OpCodes.Endfilter);
                    var rejectLabel = il.Create(OpCodes.Ldc_I4_0);
                    var filterStart = il.Create(OpCodes.Isinst, eh.CatchType);
                    var origHandlerStart = eh.HandlerStart;
                    il.InsertBefore(origHandlerStart, filterStart);
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Brfalse, rejectLabel));
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ldsfld, killIdRef));
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ldc_I4, scriptId));
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Beq, rejectLabel));  // being killed
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
                    foreach (var any in method.Body.ExceptionHandlers)
                    {
                        if (any.TryEnd     == origHandlerStart) any.TryEnd     = filterStart;
                        if (any.HandlerEnd == origHandlerStart) any.HandlerEnd = filterStart;
                    }
                }

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
                    {
                        il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4, scriptId));
                        il.InsertBefore(ins, il.Create(OpCodes.Call, bailRef));
                    }
                    // REPL/Delegate call site: StackCheck + Bail. The callvirt
                    // will push a new frame; check budget BEFORE the push so the
                    // first call per script captures StackBase, and recursion
                    // automatically catches itself as SP descends past budget.
                    // Can't Resolve() to filter true delegates from MethodInfo —
                    // Cecil reads from MemoryStream with no probing dirs.
                    else if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                             && ins.Operand is MethodReference mr
                             && (mr.DeclaringType.FullName.StartsWith("__REPL__", StringComparison.Ordinal)
                                 || mr.Name == "Invoke"))
                    {
                        il.InsertBefore(ins, il.Create(OpCodes.Call, stackRef));
                        il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4, scriptId));
                        il.InsertBefore(ins, il.Create(OpCodes.Call, bailRef));
                    }
                }
            }
        }

        var output = new MemoryStream();
        asm.Write(output);
        return output.ToArray();
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

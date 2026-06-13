using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VRage.Utils;

namespace ClientPlugin;

public sealed class CompilationResult
{
    public bool Success { get; }
    public Assembly Assembly { get; }
    public string ErrorOutput { get; }

    public CompilationResult(Assembly assembly)
    {
        Success = true;
        Assembly = assembly;
    }

    public CompilationResult(string errorOutput)
    {
        Success = false;
        ErrorOutput = errorOutput;
    }
}

public sealed class Compiler(MethodInfo guardBail, MethodInfo guardStackCheck, FieldInfo guardDead)
{
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
    private static readonly PropertyInfo DiagLoc;
    private static readonly PropertyInfo DiagId;
    private static readonly MethodInfo DiagMsg;
    private static readonly MethodInfo LocLineSpan;
    private static readonly PropertyInfo SpanStart;
    private static readonly PropertyInfo PosLine;
    private static readonly PropertyInfo PosChar;

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
    // ScriptGuard{Main,Render}'s Bail/StackCheck/Dead are the only thing that
    // differs between the two Compiler instances.

    private const string DefaultUsings = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using VRageMath;
using VRage;
using VRage.Utils;
using VRage.Collections;
using VRage.Library.Utils;
using VRage.ObjectBuilders;
using VRage.ModAPI;
using VRage.Voxels;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Components;
using VRage.Game.Components.Interfaces;
using VRage.Game.Definitions;
using VRage.Game.ObjectBuilders;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Game.GUI.TextPanel;
using VRage.Game.Utils;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.ModAPI.Network;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Network;
using Sandbox;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.ModAPI.Weapons;
using Sandbox.ModAPI.Contracts;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Gui;
using Sandbox.Game.Lights;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Components;
using Sandbox.Game.Weapons;
using Sandbox.Game.SessionComponents;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Platform;
using SpaceEngineers.Game.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Input;
using VRage.Serialization;

""";

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

    private static readonly int DefaultUsingLineCount = DefaultUsings.Count(c => c == '\n');
    private static readonly int ClassPrefixLineCount = ClassPrefix.Count(c => c == '\n');
    private static readonly int RunPrefixLineCount = RunPrefix.Count(c => c == '\n');

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
        var diagnosticType = commonAsm.GetType("Microsoft.CodeAnalysis.Diagnostic");
        var locationType = commonAsm.GetType("Microsoft.CodeAnalysis.Location");
        var lineSpanType = commonAsm.GetType("Microsoft.CodeAnalysis.FileLinePositionSpan");
        var linePositionType = commonAsm.GetType("Microsoft.CodeAnalysis.Text.LinePosition");

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
        // (game 2.9 and NuGet 4.12).
        var withAllowUnsafe = compOptsType.GetMethod("WithAllowUnsafe", [typeof(bool)])
            ?? throw new MissingMethodException(compOptsType.FullName, "WithAllowUnsafe");
        CompileOptions = withAllowUnsafe.Invoke(baseOptions, [true]);

        Emit = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "Emit"
                && m.GetParameters()[0].ParameterType == typeof(Stream)
                && m.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never
                && m.GetParameters().Skip(1).All(p => p.HasDefaultValue));

        DiagMsg = diagnosticType.GetMethod("GetMessage", [typeof(IFormatProvider)]);
        LocLineSpan = locationType.GetMethod("GetLineSpan", Type.EmptyTypes);

        EmitSuccess = emitResultType.GetProperty("Success");
        EmitDiags = emitResultType.GetProperty("Diagnostics");
        DiagLoc = diagnosticType.GetProperty("Location");
        DiagId = diagnosticType.GetProperty("Id");
        SpanStart = lineSpanType.GetProperty("StartLinePosition");
        PosLine = linePositionType.GetProperty("Line");
        PosChar = linePositionType.GetProperty("Character");
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
            if (loadContext.ContainsKey(name)) continue;

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
            if (Path.GetFileName(loc) == "VRage.Native.dll") continue;
            try { SharedReferences.Add(CallWithDefaults(CreateFromFile, null, loc)); }
            catch (Exception ex) { MyLog.Default.WriteLine($"SeMcp: failed reference {name}: {ex.Message}"); }
        }

        foreach (var (name, (asm, _)) in loadFile)
        {
            var loc = asm.Location;
            if (Path.GetFileName(loc) == "VRage.Native.dll") continue;
            try
            {
                SharedReferences.Add(CallWithDefaults(CreateFromFile, null, loc));
                SharedResolveMap[name] = asm;
            }
            catch (Exception ex) { MyLog.Default.WriteLine($"SeMcp: failed LoadFile reference {name}: {ex.Message}"); }
        }

        _sharedHandler = (_, args) =>
        {
            if (args.RequestingAssembly?.GetName().Name?.StartsWith("__REPL__") != true)
                return null;
            SharedResolveMap.TryGetValue(new AssemblyName(args.Name).Name, out var found);
            return found;
        };
        AppDomain.CurrentDomain.AssemblyResolve += _sharedHandler;

        MyLog.Default.WriteLine($"SeMcp: {SharedReferences.Count} references collected ({SharedResolveMap.Count} LoadFile)");
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
    public CompilationResult Compile(IReadOnlyList<string> usings, string classBody, string code)
    {
        classBody ??= "";

        var usingsBlock = usings == null ? "" : string.Concat(
            usings.Where(u => !string.IsNullOrWhiteSpace(u))
                  .Select(u => "using " + u.Trim() + ";\n"));

        var fullSource = DefaultUsings + usingsBlock + ClassPrefix + classBody + RunPrefix + code + ClassSuffix;

        // 0-based start lines into fullSource for each user segment. Diagnostics on
        // wrapper lines (between user segments) are attributed to the nearest
        // preceding user segment so the LLM knows which field to fix.
        var usingsStart = DefaultUsingLineCount;
        var usingsLines = usingsBlock.Count(c => c == '\n');
        var classBodyStart = usingsStart + usingsLines + ClassPrefixLineCount;
        var classBodyLines = classBody.Count(c => c == '\n');
        var codeStart = classBodyStart + classBodyLines + RunPrefixLineCount;

        var assemblyName = "__REPL__" + Interlocked.Increment(ref _counter);

        var tree = CallWithDefaults(ParseText, null, fullSource, ParseOptions);

        var treesArr = Array.CreateInstance(SyntaxTreeBase, 1);
        treesArr.SetValue(tree, 0);

        var refsArr = Array.CreateInstance(MetaRefBase, SharedReferences.Count);
        for (var i = 0; i < SharedReferences.Count; i++)
            refsArr.SetValue(SharedReferences[i], i);

        var compilation = CompilationCreate.Invoke(null, [assemblyName, treesArr, refsArr, CompileOptions]);

        using var ms = new MemoryStream();
        var emitResult = CallWithDefaults(Emit, compilation, ms);

        if (!(bool)EmitSuccess.GetValue(emitResult))
        {
            var errors = new List<string>();
            foreach (var d in (IEnumerable)EmitDiags.GetValue(emitResult))
            {
                var location = DiagLoc.GetValue(d);
                var span = LocLineSpan.Invoke(location, null);
                var startPos = SpanStart.GetValue(span);
                var compiled = (int)PosLine.GetValue(startPos);
                var col = (int)PosChar.GetValue(startPos);
                var id = (string)DiagId.GetValue(d);
                var message = (string)CallWithDefaults(DiagMsg, d);

                string field;
                int relLine;
                if (compiled >= codeStart)
                {
                    field = "code";
                    relLine = compiled - codeStart + 1;
                }
                else if (compiled >= classBodyStart)
                {
                    field = "class_body";
                    relLine = Math.Max(1, compiled - classBodyStart + 1);
                }
                else if (compiled >= usingsStart)
                {
                    field = "usings";
                    relLine = Math.Max(1, compiled - usingsStart + 1);
                }
                else
                {
                    field = "(internal)";
                    relLine = compiled + 1;
                }

                errors.Add($"{field} ({relLine},{col + 1}): error {id}: {message}");
            }
            return new CompilationResult(string.Join("\n", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var raw = ms.ToArray();
        raw = InjectTimeoutChecks(raw);
        return new CompilationResult(Assembly.Load(raw));
    }

    private static (Assembly csharp, Assembly common) LoadRoslyn()
    {
        try
        {
            var csharp = Assembly.Load(MakeAssemblyName("Microsoft.CodeAnalysis.CSharp", 4, 12, 0, 0));
            var common = Assembly.Load(MakeAssemblyName("Microsoft.CodeAnalysis", 4, 12, 0, 0));
            return (csharp, common);
        }
        catch (Exception ex)
        {
            MyLog.Default.WriteLine($"SeMcp: NuGet Roslyn unavailable ({ex.GetType().Name}), using game Roslyn");
        }

        return (
            Assembly.Load("Microsoft.CodeAnalysis.CSharp"),
            Assembly.Load("Microsoft.CodeAnalysis"));
    }

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
    // (Dead flag set by a background 1s timer in Executor.Tick; StackBase
    // captured per-script on first StackCheck):
    //   - Exception handler entry:
    //       catch → rewritten into a filter handler that rejects when Dead is
    //         set. The filter runs in CLR's pass 1 (stackless virtual unwind),
    //         so deep-recursion-plus-catch attacks unwind in constant stack.
    //       finally / fault → Bail (filter is illegal here; no caught exception).
    //   - Backward branches → Bail. Catches tight loops with no method calls.
    //   - REPL / Delegate call sites → StackCheck + Bail. Catches recursion
    //     by sampling SP; first call per script sets StackBase, subsequent
    //     calls throw if SP descends past the budget.
    // Not a security boundary — token holders already have full RCE.
    private byte[] InjectTimeoutChecks(byte[] raw)
    {
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(raw));
        var replType = asm.MainModule.Types.FirstOrDefault(t => t.Name == "__REPL__");
        if (replType == null)
            throw new InvalidOperationException("Compiled assembly missing __REPL__ type");

        var bailRef = asm.MainModule.ImportReference(guardBail);
        var deadFieldRef = asm.MainModule.ImportReference(guardDead);
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
                //    (stackless virtual unwind); rejecting catches when Dead is
                //    set costs constant stack regardless of recursion depth.
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
                //     ldsfld Dead
                //     ldc.i4.0
                //     ceq                 ; 1 if Dead==0 (accept), 0 otherwise
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
                        il.InsertAfter(eh.HandlerStart, il.Create(OpCodes.Call, bailRef));
                        continue;
                    }

                    // Single-endfilter exit. Three paths converge on it with an
                    // int32 result (0=reject, non-zero=accept):
                    //   accept  : isinst != null AND Dead == 0  →  push 1
                    //   reject1 : isinst == null (wrong type)   →  push 0
                    //   reject2 : isinst != null AND Dead != 0  →  push 0 (via ceq)
                    var endLabel = il.Create(OpCodes.Endfilter);
                    var rejectLabel = il.Create(OpCodes.Ldc_I4_0);
                    var filterStart = il.Create(OpCodes.Isinst, eh.CatchType);
                    var origHandlerStart = eh.HandlerStart;
                    il.InsertBefore(origHandlerStart, filterStart);
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Brfalse, rejectLabel));
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ldsfld, deadFieldRef));
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ldc_I4_0));
                    il.InsertBefore(origHandlerStart, il.Create(OpCodes.Ceq));               // 1 if Dead==0
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
                             && (mr.DeclaringType.FullName.StartsWith("__REPL__")
                                 || mr.Name == "Invoke"))
                    {
                        il.InsertBefore(ins, il.Create(OpCodes.Call, stackRef));
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

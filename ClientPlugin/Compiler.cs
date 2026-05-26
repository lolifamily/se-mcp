using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
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

public sealed class Compiler
{
    private static int _counter;

    private readonly MethodInfo parseText;
    private readonly MethodInfo compilationCreate;
    private readonly object parseOptions;
    private readonly object compileOptions;
    private readonly Type syntaxTreeBase;
    private readonly Type metaRefBase;
    private readonly List<object> references;

    private static readonly Regex SimpleUsing = new(@"^using\s+(static\s+)?[A-Za-z_][\w.]*\s*;$");
    private static readonly Regex AliasUsing = new(@"^using\s+[A-Za-z_]\w*\s*=\s*[A-Za-z_][\w.]*\s*;$");

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
    public static IEnumerable<object> Run(TextWriter Console)
    {

""";

    private const string ClassSuffix = """
        yield break;
    }
}
""";

    private static readonly int DefaultUsingLineCount = DefaultUsings.Count(c => c == '\n');
    private static readonly int ClassPrefixLineCount = ClassPrefix.Count(c => c == '\n');

    public Compiler()
    {
        var (csharpAsm, commonAsm) = LoadRoslyn();

        metaRefBase = commonAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
        syntaxTreeBase = commonAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree");

        var createFromFile = metaRefBase.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateFromFile" && m.GetParameters()[0].ParameterType == typeof(string));

        parseText = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree")
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "ParseText" && m.GetParameters()[0].ParameterType == typeof(string));

        compilationCreate = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.GetParameters().Length == 4
                        && m.GetParameters()[0].ParameterType == typeof(string));

        var langVerType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion");
        var parseOptsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
        parseOptions = NewWithDefaults(
            parseOptsType.GetConstructors().First(c => c.GetParameters()[0].ParameterType == langVerType),
            langVerType.GetField("Latest").GetValue(null));

        var outputKindType = commonAsm.GetType("Microsoft.CodeAnalysis.OutputKind");
        var compOptsType = csharpAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
        compileOptions = NewWithDefaults(
            compOptsType.GetConstructors().First(c => c.GetParameters()[0].ParameterType == outputKindType),
            outputKindType.GetField("DynamicallyLinkedLibrary").GetValue(null));

        references = [];
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try
            {
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc)) continue;
                if (Path.GetFileName(loc) == "VRage.Native.dll") continue;
                references.Add(CallWithDefaults(createFromFile, null, loc));
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"SeMcp: skipped reference {asm.GetName().Name}: {ex.Message}");
            }
        }

        MyLog.Default.WriteLine($"SeMcp: Roslyn {csharpAsm.GetName().Version}, {references.Count} references");
    }

    public CompilationResult Compile(string userCode)
    {
        var lines = userCode.Split('\n');
        var cut = FindUsingBoundary(lines);

        var userUsings = cut > 0 ? string.Join("\n", lines, 0, cut) + "\n" : "";
        var userBody = string.Join("\n", lines, cut, lines.Length - cut) + "\n";
        var preamble = DefaultUsings + userUsings + ClassPrefix;
        var fullSource = preamble + userBody + ClassSuffix;
        var bodyOffset = DefaultUsingLineCount + ClassPrefixLineCount;
        var bodyStart = bodyOffset + cut;
        var assemblyName = "__REPL__" + Interlocked.Increment(ref _counter);

        var tree = CallWithDefaults(parseText, null, fullSource, parseOptions);

        var treesArr = Array.CreateInstance(syntaxTreeBase, 1);
        treesArr.SetValue(tree, 0);

        var refsArr = Array.CreateInstance(metaRefBase, references.Count);
        for (var i = 0; i < references.Count; i++)
            refsArr.SetValue(references[i], i);

        dynamic compilation = compilationCreate.Invoke(null, [assemblyName, treesArr, refsArr, compileOptions]);

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!(bool)emitResult.Success)
        {
            var errors = new List<string>();
            foreach (dynamic d in emitResult.Diagnostics)
            {
                var span = d.Location.GetLineSpan();
                var compiled = (int)span.StartLinePosition.Line;
                var col = (int)span.StartLinePosition.Character;
                var offset = compiled >= bodyStart ? bodyOffset : DefaultUsingLineCount;
                var line = Math.Max(1, compiled + 1 - offset);
                errors.Add($"({line},{col + 1}): error {d.Id}: {d.GetMessage()}");
            }
            return new CompilationResult(string.Join("\n", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        return new CompilationResult(Assembly.Load(ms.ToArray()));
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
            FindLoaded("Microsoft.CodeAnalysis.CSharp"),
            FindLoaded("Microsoft.CodeAnalysis"));
    }

    private static AssemblyName MakeAssemblyName(string name, int major, int minor, int build, int rev)
    {
        var an = new AssemblyName(name) { Version = new Version(major, minor, build, rev) };
        an.SetPublicKeyToken([0x31, 0xbf, 0x38, 0x56, 0xad, 0x36, 0x4e, 0x35]);
        return an;
    }

    private static Assembly FindLoaded(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == name);

    private static int FindUsingBoundary(string[] lines)
    {
        var boundary = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                continue;
            if (!IsUsingDirective(trimmed))
                return boundary;
            boundary = i + 1;
        }
        return boundary;
    }

    private static bool IsUsingDirective(string trimmedLine)
    {
        var idx = trimmedLine.IndexOf("//", StringComparison.Ordinal);
        var effective = (idx >= 0 ? trimmedLine.Substring(0, idx) : trimmedLine).TrimEnd();
        return SimpleUsing.IsMatch(effective) || AliasUsing.IsMatch(effective);
    }

    private static object CallWithDefaults(MethodInfo method, object target, params object[] leading)
    {
        var parms = method.GetParameters();
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
        return method.Invoke(target, args);
    }

    private static object NewWithDefaults(ConstructorInfo ctor, params object[] leading)
    {
        var parms = ctor.GetParameters();
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
        return ctor.Invoke(args);
    }
}

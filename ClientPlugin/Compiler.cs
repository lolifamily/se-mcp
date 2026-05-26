using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    private readonly List<MetadataReference> references;

    private static readonly Regex SimpleUsing = new (@"^using\s+(static\s+)?[A-Za-z_][\w.]*\s*;$");

    private static readonly Regex AliasUsing = new (@"^using\s+[A-Za-z_]\w*\s*=\s*[A-Za-z_][\w.]*\s*;$");

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
        references = [];
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic)
                continue;

            try
            {
                var location = asm.Location;
                if (!string.IsNullOrEmpty(location))
                    references.Add(MetadataReference.CreateFromFile(location));
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"SeMcp: skipped reference {asm.GetName().Name}: {ex.Message}");
            }
        }
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

        var syntaxTree = CSharpSyntaxTree.ParseText(
            fullSource,
            new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var errors = result.Diagnostics.Select(d => FormatDiagnostic(d, bodyStart, bodyOffset));
            return new CompilationResult(string.Join("\n", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        return new CompilationResult(assembly);
    }

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

    private static string FormatDiagnostic(Diagnostic d, int bodyStart, int bodyOffset)
    {
        var span = d.Location.GetLineSpan();
        var compiled = span.StartLinePosition.Line;
        var offset = compiled >= bodyStart ? bodyOffset : DefaultUsingLineCount;
        var line = Math.Max(1, compiled + 1 - offset);
        var col = span.StartLinePosition.Character + 1;
        return $"({line},{col}): error {d.Id}: {d.GetMessage()}";
    }
}

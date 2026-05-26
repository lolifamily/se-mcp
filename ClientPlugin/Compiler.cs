using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private readonly int templateLineOffset;

    private const string TemplatePrefix = """
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
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using SpaceEngineers.Game.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Input;
using VRage.Serialization;

public class __REPL__
{
    public static IEnumerable<object> Run(TextWriter Console)
    {

""";

    private const string TemplateSuffix = """

        yield break;
    }
}
""";

    public Compiler()
    {
        templateLineOffset = TemplatePrefix.Count(c => c == '\n');

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
        var fullSource = TemplatePrefix + userCode + TemplateSuffix;
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
            var errors = result.Diagnostics.Select(FormatDiagnostic);
            return new CompilationResult(string.Join("\n", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        return new CompilationResult(assembly);
    }

    private string FormatDiagnostic(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var line = span.StartLinePosition.Line + 1 - templateLineOffset;
        var col = span.StartLinePosition.Character + 1;
        return $"({line},{col}): error {d.Id}: {d.GetMessage()}";
    }
}

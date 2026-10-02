namespace Shared.Se2;

// SE2 default usings, prepended so users reference the common Keen.VRage / Keen.Game2 API without
// boilerplate, and this host's words in the execute_code description (Game, Imports). Shared by the
// SE2 client (and a future SE2 server, same game API surface), so it lives once here in Shared/Se2 —
// exactly symmetric to SE1's Shared/Se1/ScriptDefaults. The namespace is Shared.Se2 — deliberately NOT
// Shared.Mcp — so Compiler (in Shared.Mcp) cannot reach it directly; the host does `using Shared.Se2`
// in its Plugin and passes these into the Executor/Compiler and ExecuteCodeTool constructors. That
// inaccessibility is the entire point of constructor injection.
//
// Usings is a STRING injected into user-script compilation at runtime (not parsed when Client2Plugin
// compiles), so a namespace that doesn't exist in the SE2 assemblies would make EVERY user script fail
// with CS0246.
internal static class ScriptDefaults
{
    // Every namespace below was verified to exist in the SE2 assemblies (block-4 audit).
    // Keen.VRage.Library.Utils (Singleton<T>) and Keen.Game2 (GameAppComponent) are the
    // load-bearing ones — the canonical session entry Singleton<VRageCore>.Instance.Engine
    // .Get<GameAppComponent>() needs both.
    public const string Usings = """
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
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Collections;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Render12.EngineComponents;
using Keen.Game2;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Client.GameSystems.PlayerControl;

""";

    // This host's name in the execute_code description (ExecuteCodeTool).
    public const string Game = "Space Engineers 2";

    // The session's entry path is the one thing a model can't guess about SE2 — GameAppComponent is
    // internal, reachable only through ignore-accessibility — so it rides in the description every
    // model reads. It used to sit in the main lane's text ("use this for ... access"), read only by a
    // model already choosing a lane. Short names are the norm here too, not a rule: see SE1's Imports.
    public const string Imports =
        "Common System, Keen.VRage and Keen.Game2 namespaces are pre-imported; "
        + "the session is Singleton<VRageCore>.Instance.Engine.Get<GameAppComponent>().ClientSession.";
}

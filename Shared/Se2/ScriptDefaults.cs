using Shared.Mcp;

namespace Shared.Se2;

// SE2 default usings + execute_code schema vocabulary, prepended/spliced so users
// reference the common Keen.VRage / Keen.Game2 API without boilerplate. Shared by the
// SE2 client (and a future SE2 server, same game API surface), so it lives once here in
// Shared/Se2 — exactly symmetric to SE1's Shared/Se1/ScriptDefaults. The namespace is
// Shared.Se2 — deliberately NOT Shared.Mcp — so Compiler (in Shared.Mcp) cannot reach it
// directly; the host does `using Shared.Se2` in its Plugin and passes these into the
// Executor/Compiler + ExecuteCodeTool constructors. That inaccessibility is the entire
// point of constructor injection.
//
// NOTE: both Usings and SchemaText are the block-4 starting point from the plan. Usings
// is a STRING injected into user-script compilation at runtime (not parsed when
// Client2Plugin compiles), so any namespace that doesn't exist in the SE2 assemblies
// would make EVERY user script fail with CS0246; SchemaText only names symbols in the
// tool description shown to the LLM. Block 4 verifies both against the real Game2
// assemblies when execute_code is exercised end-to-end, and prunes/extends before shipping.
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

    // Host-specific execute_code schema vocabulary (SE2 game API symbols). Passed to
    // ExecuteCodeTool so the tool description names Keen.* / GameAppComponent / Render12,
    // not SE1's Sandbox.* / MyAPIGateway / MyRenderThread.
    public static readonly ExecuteCodeSchemaText SchemaText = new(
        preImported: "System.*, Keen.VRage.*, Keen.Game2.*",
        shortNames: "GameAppComponent, CubeGridComponent",
        fqnExample: "Keen.Game2.GameAppComponent",
        mainDriver: "a Harmony Postfix on VRageCore.Update",
        mainApi: "Singleton<VRageCore>.Instance.Engine.Get<GameAppComponent>().ClientSession and its entities",
        renderTarget: "Render12EngineComponent.RenderFrame",
        renderAssert: "Session / scene access");
}

namespace Shared.Se1;

// SE1 default usings, prepended to every REPL script's compilation unit so users
// reference the common VRage / Sandbox / SpaceEngineers API without boilerplate, and
// this host's words in the execute_code description (Game, Imports).
// Shared by the SE1 client and dedicated server (same game API surface), so it lives
// once here in Shared/Se1. The namespace is Shared.Se1 — deliberately NOT Shared.Mcp —
// so Compiler (in Shared.Mcp) cannot reach it directly; each host does `using Shared.Se1`
// in its Plugin and passes these into the Executor/Compiler and ExecuteCodeTool
// constructors. That inaccessibility is the entire point of constructor injection.
// Shared/Se2 is the SE2 counterpart.
internal static class ScriptDefaults
{
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

    // This host's name in the execute_code description (ExecuteCodeTool).
    public const string Game = "Space Engineers";

    // Positive on purpose: short names are how a script is written, not a rule. On the client, 24
    // short names are ambiguous under Usings above (IMyInput: VRage.Input and VRage.ModAPI;
    // IMyControllableEntity: Sandbox.Game.Entities and VRage.Game.ModAPI.Interfaces), and CS0104's
    // fix is the qualified name — the old "do NOT write fully qualified names" forbade exactly that.
    // "Common" rather than "System.*": System.Threading.Tasks and System.Diagnostics, among others,
    // are not in the list, and a missing one comes back as CS0246 naming the type.
    public const string Imports =
        "Common System, VRage, VRageMath, Sandbox and SpaceEngineers.Game namespaces are pre-imported, "
        + "so short names like MySession.Static and MyCubeGrid resolve.";
}

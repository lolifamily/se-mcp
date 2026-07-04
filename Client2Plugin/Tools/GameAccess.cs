using System;
using HarmonyLib;
using Keen.Game2.Client.UI.Library;
using Keen.VRage.Core;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.EngineComponents;
using Keen.VRage.UI.Shared.ViewModels;

namespace Client2Plugin.Tools;

// Helpers for reaching game internals that are not publicly exposed.
// Uses Harmony's AccessTools so the template works without the publicizer.
internal static class GameAccess
{
    // Retrieves SharedUIComponent from the running game.
    // Keen.Game2.GameAppComponent is internal and GetSharedUI() is private, so both are resolved reflectively.
    public static SharedUIComponent GetSharedUI()
    {
        var engine = Singleton<VRageCore>.Instance?.Engine;
        if (engine == null)
            return null;

        var gameAppType = AccessTools.TypeByName("Keen.Game2.GameAppComponent");
        if (gameAppType == null)
            return null;

        var getMethod = ResolveGenericGet(engine.GetType())?.MakeGenericMethod(gameAppType);
        var gameApp = getMethod?.Invoke(engine, new object[] { default(StringId) });
        if (gameApp == null)
            return null;

        return AccessTools.Method(gameAppType, "GetSharedUI")?.Invoke(gameApp, null) as SharedUIComponent;
    }

    // Retrieves the engine's IViewModelFactory so plugin-constructed DialogViewModels
    // receive [Service]/[Configuration] dependency injection.
    public static IViewModelFactory GetViewModelFactory()
    {
        var engine = Singleton<VRageCore>.Instance?.Engine;
        if (engine == null)
            return null;

        var getMethod = ResolveGenericGet(engine.GetType())?.MakeGenericMethod(typeof(ViewModelFactoryComponent));
        return getMethod?.Invoke(engine, new object[] { default(StringId) }) as IViewModelFactory;
    }

    // Entity has two Get(StringId) methods — a generic T Get<T>(StringId) and a non-generic
    // Component Get(StringId). Parameter-list matching alone is ambiguous, so filter by
    // IsGenericMethodDefinition before constructing the generic call.
    private static System.Reflection.MethodInfo ResolveGenericGet(Type entityType) =>
        AccessTools.FirstMethod(entityType,
            m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
}

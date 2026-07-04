using System;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Core;
using Keen.VRage.Library.Utils;

namespace Client2Plugin;

// SE2 anti-cheat deny-gate probe (plan decision 7). Lives here in Client2Plugin, NOT in
// Tools/ (which is unmodified template code). Reads GameAppComponent.ClientSession /
// ServerSession reflectively — GameAppComponent is internal — using the same AccessTools
// pattern GameAccess.GetSharedUI uses, kept self-contained so Tools/ stays untouched.
internal static class SessionGate
{
    // "client session present, no local authoritative server" = a pure client connected to
    // someone else's server (MP). Single-player: both non-null. Main menu / loading: both null.
    // So this only trips for the pure-client case. Called on the main thread each frame; the
    // Executor reads the cached Config.Denied result off-thread.
    public static bool IsClientOnlySession()
    {
        var engine = Singleton<VRageCore>.Instance.Engine;

        var gameAppType = AccessTools.TypeByName("Keen.Game2.GameAppComponent");
        if (gameAppType == null)
            return false;

        var getMethod = ResolveGenericGet(engine.GetType())?.MakeGenericMethod(gameAppType);
        var gameApp = getMethod?.Invoke(engine, [default(StringId)]);
        if (gameApp == null)
            return false;

        var client = AccessTools.Property(gameAppType, "ClientSession")?.GetValue(gameApp);
        var server = AccessTools.Property(gameAppType, "ServerSession")?.GetValue(gameApp);
        return client != null && server == null;
    }

    // Engine exposes both a generic T Get<T>(StringId) and a non-generic Get(StringId);
    // filter by IsGenericMethodDefinition before building the generic call.
    private static MethodInfo ResolveGenericGet(Type engineType) =>
        AccessTools.FirstMethod(engineType,
            m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
}

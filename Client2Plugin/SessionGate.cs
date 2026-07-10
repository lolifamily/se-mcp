using Keen.Game2;
using Keen.VRage.Core;
using Keen.VRage.Library.Utils;

namespace Client2Plugin;

// SE2 anti-cheat deny-gate probe (plan decision 7). "Client session present, no local
// authoritative server" = a pure client on someone else's server (MP). Single-player: both
// non-null. Main menu / loading: GameAppComponent absent, or both sessions null. So this only
// trips for the pure-client case. Called on the main thread each frame; the Executor reads the
// cached Config.Denied result off-thread.
//
// GameAppComponent is internal in SpaceEngineers2.dll (publicized via the csproj); its
// ClientSession / ServerSession are already public. TryGet<T> yields null before the component
// is mounted, which reads as "not a client-only session".
internal static class SessionGate
{
    public static bool IsClientOnlySession()
    {
        var app = Singleton<VRageCore>.Instance.Engine.TryGet<GameAppComponent>();
        return app != null && app.ClientSession != null && app.ServerSession == null;
    }
}

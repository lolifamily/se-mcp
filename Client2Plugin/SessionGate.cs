using Keen.Game2;
using Keen.VRage.Core;
using Keen.VRage.Library.Utils;

namespace Client2Plugin;

// SE2's anti-cheat deny gate: closed for a pure client on someone else's server, a client session
// with no local server. Single player has both; the main menu and loading have neither, or no
// GameAppComponent yet, which TryGet returns as null. Main thread, once a frame (PatchMainLane).
//
// GameAppComponent is internal to SpaceEngineers2.dll, publicized through the csproj; its two
// sessions are public already.
internal static class SessionGate
{
    public static bool IsClientOnlySession()
    {
        var app = Singleton<VRageCore>.Instance.Engine.TryGet<GameAppComponent>();
        return app != null && app.ClientSession != null && app.ServerSession == null;
    }
}

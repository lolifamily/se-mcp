using Shared.Config;
using Shared.Logging;
using Shared.Patches;

namespace Shared.Plugin;

public static class Common
{
    public static IPluginLogger Logger { get; private set; }
    public static IPluginConfig Config { get; private set; }

    public static void SetPlugin(ICommonPlugin plugin)
    {
        Logger = plugin.Log;
        Config = plugin.Config;

        PatchHelpers.Configure();
    }
}
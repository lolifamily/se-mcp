using System;
using HarmonyLib;
using PluginSdk.Config;

namespace ServerPlugin.Patches;

[HarmonyPatch(typeof(ConfigSchema), nameof(ConfigSchema.Build))]
internal static class PatchConfigSchema
{
    private static void Postfix(Type configType, ConfigSchemaData __result)
    {
        if (configType != typeof(Config)) return;

        foreach (var layout in __result.Layout)
        {
            if (layout.Id == "status")
                layout.Caption = Config.StatusCaption;
        }
    }
}

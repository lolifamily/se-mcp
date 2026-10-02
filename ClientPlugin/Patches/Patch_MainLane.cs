using HarmonyLib;
using Sandbox;

namespace ClientPlugin.Patches;

// Main lane pump. IPlugin.Update runs mid-frame; this runs at the frame's end, after every
// other plugin's postfix.
[HarmonyPatch(typeof(MySandboxGame), "Update")]
internal static class PatchMainLane
{
    [HarmonyPriority(int.MinValue)]
    private static void Postfix() => Plugin.Pump();
}

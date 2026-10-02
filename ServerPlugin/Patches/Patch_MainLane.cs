using HarmonyLib;
using Sandbox;

namespace ServerPlugin.Patches;

// Main lane pump, as on the client.
[HarmonyPatch(typeof(MySandboxGame), "Update")]
internal static class PatchMainLane
{
    [HarmonyPriority(int.MinValue)]
    private static void Postfix() => Plugin.Pump();
}

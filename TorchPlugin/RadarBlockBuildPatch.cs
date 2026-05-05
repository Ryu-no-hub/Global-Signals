using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;

namespace TorchPlugin
{
    [HarmonyPatch(typeof(MyCubeGrid), "AddBlock", new[] { typeof(MyObjectBuilder_CubeBlock), typeof(bool) })]
    public static class RadarBlockBuildPatch
    {
        static void Postfix(MyCubeGrid __instance, MyObjectBuilder_CubeBlock objectBuilder)
        {
            if (Plugin.Instance == null) return;
            if (__instance == null || objectBuilder == null)
                return;

            var slim = __instance.GetCubeBlock(objectBuilder.Min);
            var func = slim?.FatBlock as MyFunctionalBlock;
            if (func == null)
                return;

            if (func.BlockDefinition?.Id.SubtypeName != "LG_D2A_R")
                return;


            func.IsWorkingChanged -= Plugin.RadarWorkingChanged;
            func.IsWorkingChanged += Plugin.RadarWorkingChanged;

            func.OnClose -= Plugin.RadarBlockClosed;
            func.OnClose += Plugin.RadarBlockClosed;

            Plugin.RadarWorkingChanged(func);

            Plugin.Instance?.Log.Info(
                $"RadarBlockBuildPatch: registered radar EntityId={func.EntityId}, OwnerId={func.OwnerId}, IsWorking={func.IsWorking}");
        }
    }
}

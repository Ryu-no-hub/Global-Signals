using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;

namespace TorchPlugin
{
    //[HarmonyPatch(typeof(MySlimBlock), "IncreaseMountLevel")]
    //public static class RadarBlockBuildPatch
    //{
    //    static void Postfix(MySlimBlock __instance)
    //    {
    //        var fat = __instance.FatBlock as MyFunctionalBlock;
    //        if (fat == null || fat.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
    //            return;

    //        fat.IsWorkingChanged -= Plugin.RadarWorkingChanged;
    //        fat.IsWorkingChanged += Plugin.RadarWorkingChanged;

    //        fat.OnClose -= Plugin.RadarBlockClosed;
    //        fat.OnClose += Plugin.RadarBlockClosed;

    //        Plugin.RadarWorkingChanged(fat);
    //        Plugin.Instance?.Log.Info("RadarBlockBuildPatch: new radar registered");
    //    }
    //}

    //[HarmonyPatch(typeof(MyCubeGrid), "OnBlockAdded")]
    //public static class RadarBlockAddedPatch
    //{
    //    static void Postfix(MyCubeGrid __instance, MySlimBlock block)
    //    {
    //        var fat = block.FatBlock as MyFunctionalBlock;
    //        if (fat == null || fat.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
    //            return;

    //        Plugin.Instance?.Log.Info("RadarBlockAddedPatch: Postfix called");

    //        fat.IsWorkingChanged -= Plugin.RadarWorkingChanged;
    //        fat.IsWorkingChanged += Plugin.RadarWorkingChanged;

    //        fat.OnClose -= Plugin.RadarBlockClosed;
    //        fat.OnClose += Plugin.RadarBlockClosed;

    //        Plugin.RadarWorkingChanged(fat);
    //        Plugin.Instance?.Log.Info("RadarBlockAddedPatch: new radar registered");
    //    }
    //}


    [HarmonyPatch(typeof(MyCubeBlock), "ChangeOwner")]
    public static class RadarBlockOwnerPatch
    {
        static void Postfix(MyCubeBlock __instance)
        {
            var func = __instance as MyFunctionalBlock;
            if (func == null || func.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
                return;

            // просто пересчитать принадлежность радара
            Plugin.RadarWorkingChanged(func);
        }
    }
}

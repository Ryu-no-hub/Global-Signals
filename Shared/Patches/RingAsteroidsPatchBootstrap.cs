using HarmonyLib;
using System;
using System.Reflection;
using VRageMath;

namespace GlobalSignals.Shared.Patches
{
    public static class RingAsteroidsPatchBootstrap
    {
        public static bool TryPatch(Harmony harmony, Action<string> logInfo, Action<string> logWarn, Action<Exception, string> logError)
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    "SERingAsteroids.RingAsteroidsComponent:CreateProceduralAsteroid",
                    new Type[]
                    {
                        typeof(int),
                        typeof(float),
                        typeof(int),
                        typeof(Vector3D),
                        typeof(string),
                        typeof(int)
                    });

                if (target == null)
                {
                    if (logWarn != null)
                        logWarn("[OreFilter] Target method not found yet. RingAsteroids mod probably has not been loaded.");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(RingAsteroidsCreateProceduralAsteroidPatch),
                    "Prefix");

                if (prefix == null)
                {
                    if (logWarn != null)
                        logWarn("[OreFilter] Prefix method not found.");
                    return false;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));

                if (logInfo != null)
                    logInfo("[OreFilter] RingAsteroids patch applied.");

                return true;
            }
            catch (Exception ex)
            {
                if (logError != null)
                    logError(ex, "[OreFilter] Failed to apply RingAsteroids patch.");
                return false;
            }
        }
    }
}
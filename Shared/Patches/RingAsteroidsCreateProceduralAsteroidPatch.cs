using System;
using System.Collections.Generic;
using System.Linq;
using GlobalSignals.Shared.Runtime;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SERingAsteroids.OctreeStorage;
using SERingAsteroids.OctreeStorage.Chunks;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace GlobalSignals.Shared.Patches
{
    public static class RingAsteroidsCreateProceduralAsteroidPatch
    {
        public static bool Prefix(
            object __instance,
            ref IMyVoxelMap __result,
            int seed,
            float size,
            int generatorSeed,
            Vector3D pos,
            string name,
            int generator)
        {
            if (!OreFilterState.Enabled)
                return true;

            try
            {
                int voxelGeneratorVersion = GetIntField(__instance, "_voxelGeneratorVersion", "voxelGeneratorVersion");
                bool disableCleanup = GetBoolField(__instance, "_disableCleanup", "disableCleanup");
                bool disableSaveLimit = GetBoolField(__instance, "_disableSaveLimit", "disableSaveLimit");

                MaterialIndexEntry[] originalPool = BuildOriginalMaterialPool(voxelGeneratorVersion);

                // ДИАГНОСТИКА: логируем версию и весь пул
                if (OreFilterState.Log != null)
                {
                    string allMats = string.Join(", ", originalPool.Select(x => x.Name).ToArray());
                    OreFilterState.Log.Info("[OreFilter] voxelGenVer=" + voxelGeneratorVersion
                        + " poolSize=" + originalPool.Length
                        + " mats=[" + allMats + "]");
                }

                MaterialIndexEntry[] filteredPool = FilterMaterialPool(originalPool);

                if (filteredPool.Length == 0)
                    filteredPool = BuildFallbackMaterialPool(voxelGeneratorVersion);

                if (filteredPool.Length == 0)
                {
                    if (OreFilterState.Log != null)
                        OreFilterState.Log.Info("[OreFilter] WARNING: filteredPool is empty, using originalPool for " + name);
                    filteredPool = originalPool;
                }

                if (OreFilterState.VerboseLogging && OreFilterState.Log != null)
                {
                    string before = string.Join(", ", originalPool.Select(x => x.Name).ToArray());
                    string after = string.Join(", ", filteredPool.Select(x => x.Name).ToArray());
                    OreFilterState.Log.Info("[OreFilter] " + name + " before=[" + before + "] after=[" + after + "]");
                }

                OctreeStorage asteroid = OctreeStorage.CreateAsteroid(
                    seed,
                    size,
                    generatorSeed,
                    0,
                    filteredPool,
                    generator);

                byte[] bytes = asteroid.GetBytes();

                IMyStorage storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(bytes);

                Vector3D spawnPos = pos - new Vector3D(
                    storage.Size.X - 1,
                    storage.Size.Y - 1,
                    storage.Size.Z - 1) / 2.0;

                IMyVoxelMap voxelMap = MyAPIGateway.Session.VoxelMaps.CreateVoxelMap(name, storage, spawnPos, 0L);
                MyEntities.RaiseEntityCreated(voxelMap as MyEntity);

                if (!disableCleanup || !disableSaveLimit)
                    voxelMap.Save = false;

                __result = voxelMap;
                return false;
            }
            catch (Exception ex)
            {
                if (OreFilterState.Log != null)
                    OreFilterState.Log.Error(ex, "[OreFilter] Failed for asteroid " + name + ", fallback to original method.");
                return true;
            }
        }

        private static MaterialIndexEntry[] BuildOriginalMaterialPool(int voxelGeneratorVersion)
        {
            return MyDefinitionManager.Static
                .GetVoxelMaterialDefinitions()
                .Where(d => d.SpawnsInAsteroids)
                .Where(d => d.MinVersion <= voxelGeneratorVersion && d.MaxVersion >= voxelGeneratorVersion)
                .Select(d => new MaterialIndexEntry { Index = d.Index, Name = d.Id.SubtypeName })
                .ToArray();
        }

        private static MaterialIndexEntry[] FilterMaterialPool(IEnumerable<MaterialIndexEntry> source)
        {
            return source
                .Where(m => !OreFilterState.IsBlacklisted(m.Name))
                .ToArray();
        }

        private static MaterialIndexEntry[] BuildFallbackMaterialPool(int voxelGeneratorVersion)
        {
            List<string> fallbacks = OreFilterState.Config != null
                ? OreFilterState.Config.StoneFallbacks
                : new List<string>();

            return MyDefinitionManager.Static
                .GetVoxelMaterialDefinitions()
                .Where(d => d.SpawnsInAsteroids)
                .Where(d => d.MinVersion <= voxelGeneratorVersion && d.MaxVersion >= voxelGeneratorVersion)
                .Where(d => fallbacks.Contains(d.Id.SubtypeName, StringComparer.OrdinalIgnoreCase))
                .Select(d => new MaterialIndexEntry { Index = d.Index, Name = d.Id.SubtypeName })
                .ToArray();
        }

        private static int GetIntField(object instance, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var field = AccessTools.Field(instance.GetType(), names[i]);
                if (field != null && field.FieldType == typeof(int))
                    return (int)field.GetValue(instance);
            }

            // Поле не найдено — логируем все доступные поля для диагностики
            if (OreFilterState.Log != null)
            {
                string tried = string.Join(", ", names);
                OreFilterState.Log.Info("[OreFilter] WARNING: int field not found on "
                    + instance.GetType().FullName + ", tried: " + tried);

                var allFields = instance.GetType().GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                string fieldList = string.Join(", ", allFields.Select(f => f.FieldType.Name + " " + f.Name).ToArray());
                OreFilterState.Log.Info("[OreFilter] Available fields on " + instance.GetType().Name + ": " + fieldList);
            }

            if (MyAPIGateway.Session != null && MyAPIGateway.Session.SessionSettings != null)
                return MyAPIGateway.Session.SessionSettings.VoxelGeneratorVersion;

            return 0;
        }

        private static bool GetBoolField(object instance, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var field = AccessTools.Field(instance.GetType(), names[i]);
                if (field != null && field.FieldType == typeof(bool))
                    return (bool)field.GetValue(instance);
            }
            return false;
        }
    }
}
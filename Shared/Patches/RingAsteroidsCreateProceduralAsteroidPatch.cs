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
                MaterialIndexEntry[] filteredPool = FilterMaterialPool(originalPool);

                if (filteredPool.Length == 0)
                    filteredPool = BuildFallbackMaterialPool(voxelGeneratorVersion);

                if (filteredPool.Length == 0)
                    filteredPool = originalPool;

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
                OctreeStorage fixedStorage = RemapForbiddenMaterials(bytes);
                if (fixedStorage == null)
                    fixedStorage = asteroid;

                byte[] fixedBytes = fixedStorage.GetBytes();
                IMyStorage storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(fixedBytes);

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

        private static OctreeStorage RemapForbiddenMaterials(byte[] bytes)
        {
            // можно логировать подробнее, но для MVP достаточно оставить logAction null
            OctreeStorage storage = OctreeStorage.ReadFrom(bytes, null, null);
            if (storage == null)
                return null;

            MaterialIndexTable table = storage.MaterialIndexTable;
            if (table == null || table.Materials == null)
                return storage;

            MaterialIndexEntry[] materials = table.Materials;

            // Построить map: имя -> индексы
            var nameToIndices = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < materials.Length; i++)
            {
                string name = materials[i].Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                List<int> list;
                if (!nameToIndices.TryGetValue(name, out list))
                {
                    list = new List<int>();
                    nameToIndices[name] = list;
                }
                list.Add(i);
            }

            // Выбрать допустимый материал для замены
            string replacementName = GetReplacementMaterialName(nameToIndices.Keys);

            if (replacementName == null)
                return storage; // нечем заменять, выходим

            List<int> replacementIndices;
            if (!nameToIndices.TryGetValue(replacementName, out replacementIndices) || replacementIndices.Count == 0)
                return storage;

            int replacementIndex = replacementIndices[0];

            // Список запрещённых материалов (по имени)
            var forbiddenNames = new List<string>();
            forbiddenNames.AddRange(OreFilterState.Config.BlacklistedOres);

            // Пройти по материалам и переписать запрещённые на replacement
            for (int i = 0; i < materials.Length; i++)
            {
                string name = materials[i].Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (forbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    // Здесь есть две стратегии:
                    // 1) сделать эту запись точной копией replacement
                    materials[i].Index = materials[replacementIndex].Index;
                    materials[i].Name = materials[replacementIndex].Name;

                    // 2) или вообще занулить/объединить — но 1) проще и безопаснее
                }
            }

            table.Materials = materials;
            storage.MaterialIndexTable = table;

            return storage;
        }

        private static string GetReplacementMaterialName(IEnumerable<string> availableNames)
        {
            // Простая стратегия: взять первый "камень" из списка конфигурации или по названию
            // Например:
            var preferred = new[] { "Stone_01", "Stone_02", "Stone_03", "Stone", "SmallMoonRocks" };
            foreach (string p in preferred)
            {
                foreach (string name in availableNames)
                {
                    if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }

            // Если ничего не подошло, просто взять любой не запрещённый
            foreach (string name in availableNames)
            {
                if (!OreFilterState.IsBlacklisted(name))
                    return name;
            }

            return null;
        }

        private static MaterialIndexEntry[] BuildOriginalMaterialPool(int voxelGeneratorVersion)
        {
            return MyDefinitionManager.Static
                .GetVoxelMaterialDefinitions()
                .Where(d => d.SpawnsInAsteroids)
                .Where(d => d.MinVersion <= voxelGeneratorVersion && d.MaxVersion >= voxelGeneratorVersion)
                .Select(d => new MaterialIndexEntry
                {
                    Index = d.Index,
                    Name = d.Id.SubtypeName
                })
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
                .Select(d => new MaterialIndexEntry
                {
                    Index = d.Index,
                    Name = d.Id.SubtypeName
                })
                .ToArray();
        }

        private static int GetIntField(object instance, params string[] names)
        {
            int i;
            for (i = 0; i < names.Length; i++)
            {
                var field = AccessTools.Field(instance.GetType(), names[i]);
                if (field != null && field.FieldType == typeof(int))
                    return (int)field.GetValue(instance);
            }

            if (MyAPIGateway.Session != null && MyAPIGateway.Session.SessionSettings != null)
                return MyAPIGateway.Session.SessionSettings.VoxelGeneratorVersion;

            return 0;
        }

        private static bool GetBoolField(object instance, params string[] names)
        {
            int i;
            for (i = 0; i < names.Length; i++)
            {
                var field = AccessTools.Field(instance.GetType(), names[i]);
                if (field != null && field.FieldType == typeof(bool))
                    return (bool)field.GetValue(instance);
            }

            return false;
        }
    }
}
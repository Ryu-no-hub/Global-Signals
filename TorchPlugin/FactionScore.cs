#define USE_HARMONY

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Logging;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace TorchPlugin
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class FactionScoreManager
    {
        //public DateTime lastScoreTime = DateTime.Now;
        public Dictionary<long, int> factionScores = new Dictionary<long, int>();
        private static readonly Dictionary<MyDefinitionId, double> WeaponDps = new Dictionary<MyDefinitionId, double>(MyDefinitionId.Comparer);

        private static List<string> uncapturablePlanets = new List<string> { "Overvent" };
        private static Dictionary<MyPlanet, List<KeyValuePair<Vector3D, bool>>> planetPoints = new Dictionary<MyPlanet, List<KeyValuePair<Vector3D, bool>>>();
        static double capturableVolumeTotal = 0;
        static Dictionary<MyPlanet, double> capturableVolumes = new Dictionary<MyPlanet, double>();
        //static readonly MyDefinitionId MastBeaconDefId = new MyDefinitionId(typeof(MyObjectBuilder_Beacon), "LG_Ship_Mast_3");
        static readonly List<MyDefinitionId> MastBeaconDefIdList = new List<MyDefinitionId> 
        { 
            new MyDefinitionId(typeof(MyObjectBuilder_Beacon), "TerritoryBeacon"),
            new MyDefinitionId(typeof(MyObjectBuilder_Beacon), "TerritoryBeaconRound"),
            new MyDefinitionId(typeof(MyObjectBuilder_Beacon), "LG_Ship_Mast_3"), 
        };

        static bool printed = false;


        static readonly Dictionary<long, DateTime> _lastMovement = new Dictionary<long, DateTime>();
        public static readonly Dictionary<long, TimeSpan> _activeTime = new Dictionary<long, TimeSpan>();
        public static readonly Dictionary<long, TimeSpan> _activeTime2Weeks = new Dictionary<long, TimeSpan>();
        public static readonly Dictionary<long, DateTime> _lastSeen = new Dictionary<long, DateTime>();
        public static readonly Dictionary<long, List<ActivityRecord>> _recentActivity = new Dictionary<long, List<ActivityRecord>>();

        const float CaptureThreshold = 0.8f;
        public static Dictionary<long, Dictionary<long, double>> factionPlanetCoveredFraction = new Dictionary<long, Dictionary<long, double>>();

        public static IPluginLogger Log { get; private set; }
        public class ActivityRecord
        {
            public DateTime Timestamp { get; set; }
            public double Hours { get; set; }
        }
        public static void Init(IPluginLogger logger)
        {
            Log = logger;
            WeaponDps.Clear();
        }


        private static double GetWeaponDps(MyDefinitionId gunBlockDefId)
        {
            if (WeaponDps.TryGetValue(gunBlockDefId, out var cached))
                return cached;

            double dps = CalculateWeaponDpsFromDefinitions(gunBlockDefId);
            WeaponDps[gunBlockDefId] = dps;
            return dps;
        }

        private static double CalculateWeaponDpsFromDefinitions(MyDefinitionId gunBlockDefId)
        {
            if (gunBlockDefId.SubtypeName=="LargeRailgun")
            {
                if (Plugin.debug) Log?.Info($"CalculateWeaponDpsFromDefinitions: {gunBlockDefId.SubtypeName}, DPS = {833.0} HARDCODE");
                return 833.0;
            }
            else if ((gunBlockDefId.SubtypeName == "SmallRailgunMy") || (gunBlockDefId.SubtypeName == "SmallRailgun"))
            {
                if (Plugin.debug) Log?.Info($"CalculateWeaponDpsFromDefinitions: {gunBlockDefId.SubtypeName},  DPS = {400.0} HARDCODE");
                return 400.0;
            }


            // 1) Get the cube block definition for this gun
            var blockDef = MyDefinitionManager.Static.GetCubeBlockDefinition(gunBlockDefId) as MyWeaponBlockDefinition;
            if (blockDef == null)
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: blockDef == null for gun: '{gunBlockDefId}'");
                return 0.0;
            }

            if (blockDef.WeaponDefinitionId == null)
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: WeaponDefinitionId == null for gun: '{gunBlockDefId}'");
                return 0.0;
            }

            // 2) Get the weapon definition it references
            MyWeaponDefinition weaponDef;
            if (!MyDefinitionManager.Static.TryGetWeaponDefinition(blockDef.WeaponDefinitionId, out weaponDef))
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: WeaponDefinition == null for gun: '{gunBlockDefId}', blockDef.WeaponDefinitionId = {blockDef.WeaponDefinitionId}");

                if (!printed && Plugin.debug)
                {
                    printed = true;
                    var mgr = MyDefinitionManager.Static;
                    var weapons = mgr.GetDefinitionsOfType<MyWeaponDefinition>();
                    Log.Info($"DumpAllWeaponDefinitions: count = {weapons.Count}");
                    foreach (var w in weapons)
                    {
                        var id = w.Id;
                        Log.Info($"WeaponDef: type='{id.TypeId}', subtype='{id.SubtypeName}'");
                    }
                }
                return 0.0;
            }

            // 3) Get ammo magazine and ammo definition (needed for ALL weapon types)
            if (weaponDef.AmmoMagazinesId == null || weaponDef.AmmoMagazinesId.Length == 0)
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: weaponDef.AmmoMagazinesId == null for gun: '{gunBlockDefId}'");
                return 0.0;
            }

            var magDefId = weaponDef.AmmoMagazinesId[0];
            if (magDefId == null)
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: magDefId == null for gun {gunBlockDefId}, block='{blockDef.DisplayNameText}'");
                return 0.0;
            }
            var magDef = MyDefinitionManager.Static.GetAmmoMagazineDefinition(magDefId);
            if (magDef == null)
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: magDef == null for gun {gunBlockDefId}, magId={magDefId}");
                return 0.0;
            }

            var ammoDefId = magDef.AmmoDefinitionId;
            var ammoDef = MyDefinitionManager.Static.GetAmmoDefinition(ammoDefId);
            if (ammoDef == null)
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: ammoDef == null for gun {gunBlockDefId}, ammoDefId={ammoDefId}");
                return 0.0;
            }

            double damagePerShot = GetDamagePerShot(ammoDef);
            double reloadTime = weaponDef.ReloadTime / 1000.0;

            // ProjectileAmmoData contains RateOfFire, ShotsInBurst etc. for projectile weapons
            //bool isProjectileGun = weaponDef.WeaponAmmoDatas != null && weaponDef.WeaponAmmoDatas.Length > 0;
            //var weaponAmmoData = isProjectileGun ? weaponDef.WeaponAmmoDatas[0] : null;
            MyWeaponDefinition.MyWeaponAmmoData dataBullets = weaponDef.WeaponAmmoDatas[(int)MyAmmoType.HighSpeed];
            MyWeaponDefinition.MyWeaponAmmoData dataMissiles = weaponDef.WeaponAmmoDatas[(int)MyAmmoType.Missile];

            int shotsInBurst;
            double rateOfFireRpm, roundsPerSecond, burstFireTime, dpsBeforeReload, dps;
            if (dataBullets != null)
            {
                rateOfFireRpm = dataBullets.RateOfFire;
                roundsPerSecond = rateOfFireRpm / 60.0;
                shotsInBurst = dataBullets.ShotsInBurst > 0 ? dataBullets.ShotsInBurst : 1;

                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: Is projectile, rateOfFireRpm={rateOfFireRpm}: shotsInBurst={shotsInBurst}");
            }
            else if (dataMissiles != null)
            {
                rateOfFireRpm = dataMissiles.RateOfFire;
                roundsPerSecond = rateOfFireRpm / 60.0;
                shotsInBurst = dataMissiles.ShotsInBurst > 0 ? dataMissiles.ShotsInBurst : 1;

                // shotsInBurst = magDef.Capacity;
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: Is missile, rateOfFireRpm={rateOfFireRpm}: shotsInBurst={shotsInBurst}");
            }
            else
            {
                if (Plugin.debug) Log.Info($"CalculateWeaponDpsFromDefinitions: dataBullets == null AND dataMissiles == null");
                return 0.0;
            }

            dpsBeforeReload = damagePerShot * roundsPerSecond;
            burstFireTime = shotsInBurst / roundsPerSecond;
            dps = dpsBeforeReload * (burstFireTime / (burstFireTime + reloadTime));

            if (Plugin.debug) Log?.Info($"CalculateWeaponDpsFromDefinitions: {gunBlockDefId.SubtypeName} ({blockDef.DisplayNameText}) DPS = {dps}");

            return dps;
        }


        private static double GetDamagePerShot(MyAmmoDefinition ammoDef)
        {
            var projectileDef = ammoDef as MyProjectileAmmoDefinition;
            double damage = 0f;
            if (projectileDef != null)
            {
                // Main hit damage per projectile
                damage = projectileDef.ProjectileMassDamage;
                if (Plugin.debug) Log.Info($"GetDamagePerShot: projectileDef.ProjectileMassDamage = {damage}");

            }

            var missileDef = ammoDef as MyMissileAmmoDefinition;
            if (missileDef != null)
            {
                double missileRadius = missileDef.MissileExplosionRadius;

                if (missileDef.MissileHealthPool > 0)
                {
                    // Artillery & other stuff
                    damage = missileDef.MissileHealthPool;
                }
                else
                {
                    // Missile
                    double missileDamage = missileDef.MissileExplosionDamage;
                    damage = missileDamage * 0.92 * Math.Max(1,
                    (Math.PI * Math.Pow(missileRadius, 2) / 6.25) / 2) * 0.8; // last is an empyrical value to correspond with reality better

                    if (Plugin.debug) Log.Info($"GetDamagePerShot: missileDamage = {missileDamage}");
                    if (Plugin.debug) Log.Info($"GetDamagePerShot: missileDef.MissileExplosionRadius = {missileDef.MissileExplosionRadius}");
                }

                if (Plugin.debug) Log.Info($"GetDamagePerShot: damage = {damage}");
            }

            return damage;
        }

        private static float GetProductionRate(IMyProductionBlock p)
        {
            var assembler = p as IMyAssembler;
            if (assembler != null)
            {
                var assemblerDef = MyDefinitionManager.Static.GetCubeBlockDefinition(assembler.BlockDefinition) as MyAssemblerDefinition;
                if (assemblerDef == null)
                {
                    if (Plugin.debug) Log.Info("GetProductionRate: assemblerDef == null");
                    return 1;
                }

                float baseSpeed = assemblerDef.AssemblySpeed;
                var upgrades = assembler.UpgradeValues;

                // Speed modules increase speed
                upgrades.TryGetValue("Productivity", out var speedMod); // 0.5 per 1 slot (2 slots taken by module), additive

                float speedMult = 1f + speedMod;
                if (Plugin.debug) Log.Info($"GetProductionRate: assemblerDef.AssemblySpeed = {baseSpeed}, prodMult = {speedMult}");
                if (Plugin.debug) Log.Info($"GetProductionRate: result = {baseSpeed * speedMult}");

                return baseSpeed * speedMult;

            }

            var refinery = p as IMyRefinery;
            if (refinery != null)
            {
                var refineryDef = MyDefinitionManager.Static.GetCubeBlockDefinition(refinery.BlockDefinition) as MyRefineryDefinition;
                if (refineryDef == null)
                {
                    if (Plugin.debug) Log.Info("GetProductionRate: refineryDef == null");
                    return 1;
                }

                float baseSpeed = refineryDef.RefineSpeed; 
                var upgrades = refinery.UpgradeValues;

                // Speed modules increase speed
                upgrades.TryGetValue("Productivity", out var speedMod); // 0.5 per 1 slot (2 slots taken by module), additive
                upgrades.TryGetValue("Effectiveness", out var yieldMod); // 1.0905077 per 1 slot (2 slots taken by module), Multiplicative

                float speedMult = 1f + speedMod;
                float yieldMult = yieldMod == 0 ? 1 : yieldMod;

                if (Plugin.debug) Log.Info($"GetProductionRate: refineryDef.RefineSpeed = {baseSpeed}, speedMult = {speedMult}, yieldMult = {yieldMult}");
                if (Plugin.debug) Log.Info($"GetProductionRate: result = {baseSpeed * speedMult * yieldMult}");
                return baseSpeed * speedMult * yieldMult;

            }

            return 1;
        }


        public static int CalculatePlayerPower(long playerIdentityId)
        {
            double totalPower = 0.0;

            foreach (var group in MyCubeGridGroups.Static.Physical.Groups)
            {
                foreach (var node in group.Nodes)
                {
                    // Check sanity
                    var grid = node.NodeData;
                    if (grid == null || grid.Physics == null)
                        continue;

                    if (grid.IsNpcSpawnedGrid)
                        continue;

                    // Check owner
                    var owners = grid.BigOwners;
                    if (owners == null || owners.Count == 0 || !owners.Contains(playerIdentityId))
                        continue;

                    //Log.Info($"CalculatePlayerPower: grid: {grid.DisplayName}");

                    // PCU
                    int pcu = grid.BlocksPCU;
                    if (pcu <= 0)
                        continue;
                    //Log.Info($"CalculatePlayerPower: PCU = {pcu}");

                    // Integrity
                    var blocks = new HashSet<MySlimBlock>();
                    blocks = grid.GetBlocks();
                    float integrity = 0f;
                    foreach (var b in blocks)
                        integrity += b.BlockDefinition.MaxIntegrity;
                    //Log.Info($"CalculatePlayerPower: integrity = {integrity}");

                    List<IMyUserControllableGun> weaponsList = new List<IMyUserControllableGun>();
                    MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid).GetBlocksOfType(weaponsList, w => w.IsFunctional);

                    double gridDps = 0.0;
                    foreach (var weapon in weaponsList)
                    {
                        double dps = GetWeaponDps(weapon.BlockDefinition);
                        gridDps += dps;
                        //Log.Info($"CalculatePlayerPower: weaponType: {weapon.BlockDefinition.TypeIdString}, weaponSubType: {weapon.BlockDefinition.SubtypeName}, dps = {dps}");
                    }
                    if (Plugin.debug) Log.Info($"CalculatePlayerPower:");


                    var terminalSystem = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);

                    var assemblers = new List<IMyAssembler>();
                    MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid).GetBlocksOfType(assemblers, w => w.IsFunctional);
                    var refineries = new List<IMyRefinery>();
                    MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid).GetBlocksOfType(refineries, w => w.IsFunctional);

                    double gridProduction = 0.0;
                    double assemblersProduction = 0.0;
                    foreach (var a in assemblers)
                    {
                        float assemblerProduction = GetProductionRate(a);
                        gridProduction += assemblerProduction;
                        assemblersProduction += assemblerProduction;
                        if (Plugin.debug) Log.Info($"CalculatePlayerPower: productionType: {a.BlockDefinition.SubtypeName}, assemblerProduction = {assemblerProduction}");
                    }
                    //Log.Info($"CalculatePlayerPower: assemblersProduction = {assemblersProduction}");

                    double refineriesProduction = 0.0;
                    foreach (var r in refineries)
                    {
                        float refineryProduction = GetProductionRate(r);
                        gridProduction += refineryProduction;
                        refineriesProduction += refineryProduction;
                        if (Plugin.debug) Log.Info($"CalculatePlayerPower: productionType: {r.BlockDefinition.SubtypeName}, refineryProduction = {refineryProduction}");
                    }
                    //Log.Info($"CalculatePlayerPower: refineriesProduction = {refineriesProduction}");
                    //Log.Info($"CalculatePlayerPower: Production = {gridProduction}");


                    // Formula:
                    // GridPower = PCU^0.2 * Integrity^0.3 * (DPS + 1)^0.3 + Production^0.6
                    double gridPower =
                        Math.Pow(pcu, 0.2) *
                        Math.Pow(integrity, 0.3) *
                        Math.Pow(gridDps + 1.0, 0.3) +
                        Math.Pow(gridProduction, 0.6);

                    if (Plugin.debug)
                    {
                        Log.Info($"CalculatePlayerPower: '{grid.DisplayName}' POWER = {gridPower}");
                        Log.Info($"CalculatePlayerPower: Assemblers({assemblers.Count}): {string.Join(", ", assemblers.Select(a => a.CustomName))}. assemblersProduction = {assemblersProduction}");
                        Log.Info($"CalculatePlayerPower: Refinaries({refineries.Count}): {string.Join(", ", refineries.Select(a => a.CustomName))}. refineriesProduction = {refineriesProduction}");
                        Log.Info($"CalculatePlayerPower: gridProduction = {gridProduction}, gridProduction^0.6 = {Math.Pow(gridProduction, 0.6)}");
                        Log.Info($"CalculatePlayerPower: Weapons({weaponsList.Count}): {string.Join(", ", weaponsList.Select(a => a.CustomName))}. gridDps = {gridDps}, (gridDps + 1.0)^0.3 = {Math.Pow(gridDps + 1.0, 0.3)}");
                        Log.Info($"CalculatePlayerPower: pcu = {pcu}, pcu^0.2 = {Math.Pow(pcu, 0.2)}");
                        Log.Info($"CalculatePlayerPower: integrity = {integrity}, integrity^0.3 = {Math.Pow(integrity, 0.3)}");
                    }

                    totalPower += gridPower;
                }
            }

            if (totalPower > int.MaxValue) return int.MaxValue;
            if (totalPower < int.MinValue) return int.MinValue;
            return (int)totalPower / 10;
        }


        public static float CalculatePlayerTerritoryControl(long identityId)
        {
            string playerName = Plugin.GetPlayerNameByIdentity(identityId);
            if (playerName == null)
            {
                IMyPlayer player = MyAPIGateway.Players.TryGetIdentityId(identityId);
                playerName = player.DisplayName;
            }

            // Get player grids
            var playerGrids = new HashSet<MyCubeGrid>();

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e =>
            {
                if (!(e is MyCubeGrid grid))
                    return false;

                if (grid.IsPreview || grid.Physics == null || grid.Closed || grid.MarkedForClose)
                    return false;

                // Skip NPC grids
                if (grid.IsNpcSpawnedGrid)
                    return false;

                var owners = grid.BigOwners;
                if (owners == null || owners.Count == 0)
                    return false;

                return owners.Contains(identityId);
            });

            foreach (var e in entities)
            {
                if (e is MyCubeGrid g)
                    playerGrids.Add(g);
            }

            if (playerGrids.Count == 0)
                return 0;

            // Precompute per-planet sampling points
            if (planetPoints.Count == 0)
                InitPlanetPoints();

            // For each planet, collect player's beacons and compute coverage
            double coveredScore = 0;
            double coveredFraction = 0;

            var planetPointsCopy = planetPoints.Keys;
            var planets = new List<MyPlanet>(planetPoints.Keys);
            //foreach (var kv in planetPointsCopy)
            foreach (var planet in planets)
            {
                //var planet = kv.Key;
                //var points = kv.Value;
                double coveredPlanetScore = 0;

                var points = planetPoints[planet];

                if (points == null || points.Count == 0)
                    continue;

                // Collect player beacons on this planet
                var controlStations = new List<IMyBeacon>();

                foreach (var grid in playerGrids)
                {
                    var gridPlanet = MyGamePruningStructure.GetClosestPlanet(grid.PositionComp.WorldAABB.Center);
                    if (gridPlanet == null || gridPlanet.EntityId != planet.EntityId)
                        continue;

                    var fatBlocks = grid.GetFatBlocks();

                    foreach (var block in fatBlocks)
                    {
                        if (!(block is IMyBeacon beacon))
                            continue;

                        if (!beacon.IsWorking)
                            continue;

                        var def = beacon.SlimBlock.BlockDefinition.Id;
                        if (!MastBeaconDefIdList.Contains(def))
                            continue;

                        controlStations.Add(beacon);
                        if (Plugin.debug) Log.Info($"CalculatePlayerTerritoryControl: Found control station on grid {beacon.CubeGrid.DisplayName}");
                    }
                }

                if (controlStations.Count == 0)
                    continue;

                int coveredPlanetPoints = 0;
                //int idx = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var kv = points[i];              // KeyValuePair<Vector3D,bool>
                    var pos = kv.Key;                // Vector3D
                    bool inAny = false;

                    foreach (var b in controlStations)
                    {
                        double r = b.Radius / 2;
                        if (Plugin.debug) Log.Info($"CalculatePlayerTerritoryControl: Station control radius = {r}");
                        if (Vector3D.DistanceSquared(pos, b.GetPosition()) <= r * r)
                        {
                            inAny = true;
                            break;
                        }
                    }

                    points[i] = new KeyValuePair<Vector3D, bool>(pos, false);
                    if (inAny)
                    {
                        if (Plugin.debug) Log.Info($"CalculatePlayerTerritoryControl: point {i} ({pos.X},{pos.Y},{pos.Z}) is covered");

                        // update bool flag to true
                        points[i] = new KeyValuePair<Vector3D, bool>(pos, true);

                        coveredPlanetPoints++;
                    }
                }

                double coveragePlanetFraction = (double)coveredPlanetPoints / points.Count;
                coveredFraction += coveragePlanetFraction;

                coveredPlanetScore = coveragePlanetFraction * planet.AverageRadius / 1000;
                coveredScore += coveredPlanetScore;

                if (Plugin.debug)
                {
                    Log.Info($"CalculatePlayerTerritoryControl: coveredPlanetPoints = {coveredPlanetPoints}");
                    Log.Info($"CalculatePlayerTerritoryControl: coveragePlanetFraction = {coveragePlanetFraction}");
                    Log.Info($"CalculatePlayerTerritoryControl: coveredPlanetScore = {coveredPlanetScore}");
                    Log.Info($"CalculatePlayerTerritoryControl: player '{playerName}' controls {Math.Round(coveragePlanetFraction * 100, 2)}% of planet '{planet.StorageName}' territory.\n Control Score: {coveredPlanetScore}");
                }

                // Capture mechanics sum
                var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identityId);
                if (faction != null)
                {
                    long factionId = faction.FactionId;

                    string planetName = GetBasePlanetName(planet);

                    if (!factionPlanetCoveredFraction.TryGetValue(factionId, out var factionPlanetsCoverage))
                    {
                        factionPlanetsCoverage = new Dictionary<long, double>();
                        factionPlanetCoveredFraction[factionId] = factionPlanetsCoverage;
                    }

                    if (!factionPlanetsCoverage.TryGetValue(planet.EntityId, out var planetCoverage))
                        factionPlanetsCoverage[planet.EntityId] = coveragePlanetFraction;
                    else
                        factionPlanetsCoverage[planet.EntityId] = planetCoverage + coveragePlanetFraction;

                }
                /////////
            }

            //if (capturableVolume <= 0)
            //    CalculateCapturableVolume();

            //Log.Info($"CalculatePlayerTerritoryControl: capturableVolume: {capturableVolume}");
            if (Plugin.debug) Log.Info($"CalculatePlayerTerritoryControl: player '{playerName}' controls {Math.Round(coveredFraction * 100, 2)}% of global territory.\n Control Score: {coveredScore}");

            return (float)coveredScore;
        }


        public static StringBuilder CapturedResults(long factionId, StringBuilder resultSB)
        {
            IMyFaction faction = MySession.Static.Factions.TryGetFactionById(factionId);
            if (faction == null)
            {
                resultSB.AppendLine($"No faction for factionId '{factionId}'");
                return resultSB;
            }

            var storage = Plugin.Instance?._scores;
            if (storage == null)
            {
                resultSB.AppendLine("storage == null");
                return resultSB;
            }

            if (!factionPlanetCoveredFraction.TryGetValue(factionId, out var factionPlanetsCoverage))
            {
                factionPlanetsCoverage = new Dictionary<long, double>();
                factionPlanetCoveredFraction[factionId] = factionPlanetsCoverage;
            }
            string caprutedPlanets = "";
            double capturedVolume = 0.0;

            foreach (var kv in planetPoints)
            {
                var planet = kv.Key;
                string planetName = GetBasePlanetName(planet);

                if (!storage.Factions.TryGetValue(factionId, out FactionScoreInfo factionScoreInfo))
                {
                    factionScoreInfo = new FactionScoreInfo
                    {
                        FactionId = factionId,
                        Name = faction.Name,
                        Tag = faction.Tag,
                        Score = 0,
                        Power = 0,
                        TerritoryControl = 0f,
                        Activity = 0,
                        Hours2W = 0f,
                        CapturedPlanets = new List<string>(),
                        CreatedUtc = DateTime.UtcNow
                    };
                    storage.Factions[factionId] = factionScoreInfo;
                }

                string coverage = "";
                bool capturedNow = false;
                if (!factionPlanetsCoverage.TryGetValue(planet.EntityId, out var coveragePlanetFraction))
                {
                    coveragePlanetFraction = 0.0;
                    //Log.Info($"CapturedResults: faction '{faction.Name}' has no coverage entry for planet '{planetName}', treating as 0.");
                }
                else
                {
                    capturedNow = coveragePlanetFraction >= CaptureThreshold;
                    coverage = capturedNow ? "Capture" : "Control";
                    coverage += $" coverage of '{planetName}' = {coveragePlanetFraction * 100:F1}%";
                    //Log.Info($"CapturedResults: faction '{faction.Name}' coverage for planet '{planetName}' = {coveragePlanetFraction}");
                }

                

                if (capturedNow)
                {
                    if(string.IsNullOrEmpty(caprutedPlanets))
                        caprutedPlanets = planetName;
                    else
                        caprutedPlanets += ", " + planetName;

                    if (!factionScoreInfo.CapturedPlanets.Contains(planetName))
                    {
                        factionScoreInfo.CapturedPlanets.Add(planetName);
                        resultSB.AppendLine($"Planet '{planetName}' CAPTURED! Coverage = {coveragePlanetFraction * 100:F1}% / 80%");
                    }

                    if (Plugin.debug) Log.Info($"CapturedResults: Planet '{planetName}' CAPTURED by '{faction.Tag}', coveragePlanetFraction = {coveragePlanetFraction}");

                    capturedVolume += capturableVolumes[planet];
                }
                else
                {
                    if (factionScoreInfo.CapturedPlanets.Contains(planetName))
                    {
                        // Drop capture if control falls below threshold
                        factionScoreInfo.CapturedPlanets.Remove(planetName);
                        if (Plugin.debug) Log.Info($"CapturedResults: Planet '{planetName}' capture LOST for '{faction.Tag}', coveragePlanetFraction = {coveragePlanetFraction}");
                        resultSB.AppendLine($"Planet '{planetName}' LOST! Coverage = {coveragePlanetFraction * 100:F1}% / 80%");
                    }
                    else if (!string.IsNullOrEmpty(coverage))
                        resultSB.AppendLine(coverage);
                }
            }

            if(!string.IsNullOrEmpty(caprutedPlanets))
                resultSB.AppendLine($"Captured planets: {caprutedPlanets}. \nCaptured volume %: {capturedVolume/capturableVolumeTotal * 100:F2}% ");
            return resultSB;
        }

        private static void InitPlanetPoints()
        {
            foreach (var planet in MyPlanets.GetPlanets())
            {
                if (uncapturablePlanets.Contains(GetBasePlanetName(planet)))
                    continue;

                const int pointCount = 500;

                double innerR = planet.AverageRadius;
                double outerR = planet.MaximumRadius * Math.Pow((planet.Generator.SurfaceGravity / 0.05), 1.0 / 7.0);

                var center = planet.PositionComp.WorldAABB.Center;
                var list = new List<KeyValuePair<Vector3D, bool>>(pointCount);

                for (int i = 0; i < pointCount; i++)
                {
                    int seed = MakeSeed(planet.EntityId, i);
                    Vector3D p = GeneratePointInShell(center, innerR, outerR, seed);
                    list.Add(new KeyValuePair<Vector3D, bool>(p, false));
                }

                planetPoints[planet] = list;
            }
        }

        private static Vector3D GeneratePointInShell(Vector3D planetCenter,
                                             double innerRadius,
                                             double outerRadius,
                                             int seed)
        {
            var rnd = new Random(seed);

            // random direction (uniform on sphere)
            double u = rnd.NextDouble();
            double v = rnd.NextDouble();
            double theta = 2.0 * Math.PI * u;
            double phi = Math.Acos(2.0 * v - 1.0);
            double sinPhi = Math.Sin(phi);

            Vector3D dir = new Vector3D(
                sinPhi * Math.Cos(theta),
                sinPhi * Math.Sin(theta),
                Math.Cos(phi));

            // random radius uniform in volume between inner/outer
            double uR = rnd.NextDouble();
            double rInner3 = innerRadius * innerRadius * innerRadius;
            double rOuter3 = outerRadius * outerRadius * outerRadius;
            double r = Math.Pow(rInner3 + uR * (rOuter3 - rInner3), 1.0 / 3.0);

            return planetCenter + dir * r;
        }

        private static int MakeSeed(long planetId, int index)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + planetId.GetHashCode();
                h = h * 31 + index;
                return h;
            }
        }

        //public static void DrawDebugPoints()
        //{
        //    if (planetPoints == null || planetPoints.Count == 0)
        //        return;

        //    Color color = Color.Lime;
        //    float radius = 25f; // visual size of markers

        //    foreach (var kv in planetPoints)
        //    {
        //        var points = kv.Value;
        //        if (points == null)
        //            continue;

        //        foreach (var p in points)
        //        {
        //            MatrixD worldMatrix = MatrixD.CreateTranslation(p);
        //            MySimpleObjectDraw.DrawTransparentSphere(ref worldMatrix, radius, ref color, MySimpleObjectRasterizer.Solid, 1);
        //        }
        //    }
        //}

        public static void RemoveDebugGpsFromPlayer(long identityId)
        {
            var gpsCollection = MyAPIGateway.Session?.GPS as MyGpsCollection;
            if (gpsCollection == null)
                return;

            var existing = MyAPIGateway.Session.GPS.GetGpsList(identityId);
            if (existing != null)
            {
                foreach (var gps in existing)
                {
                    if (gps.Description != null && gps.Description.Contains("TerritoryDebug"))
                        MyAPIGateway.Session.GPS.RemoveGps(identityId, gps);
                }
            }
        }


        public static void SendDebugGpsToPlayer(long identityId, string namePrefix = "TerritoryPoint")
        {
            if (planetPoints == null || planetPoints.Count == 0)
                InitPlanetPoints();

            // get GPS collection
            var gpsCollection = MyAPIGateway.Session?.GPS as MyGpsCollection;
            if (gpsCollection == null)
                return;

            RemoveDebugGpsFromPlayer(identityId);

            int idx = 0;
            foreach (var kv in planetPoints)
            {
                var planet = kv.Key;
                var points = kv.Value;
                if (points == null)
                    continue;


                //var defId = planet.Generator.Id;
                //if (!string.Equals(defId.SubtypeName, "EarthLike", StringComparison.OrdinalIgnoreCase))
                //    continue;

                // Option 2 (fallback): by storage name
                if (!planet.StorageName.Contains("EarthLike", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var p in points)
                {
                    var gps = new MyGps
                    { 
                        Coords = p.Key,
                        //Name = $"{namePrefix} {idx}",
                        Name = $"{idx}",
                        DisplayName = $"{namePrefix} {idx}",
                        Description = "TerritoryDebug",
                        GPSColor = p.Value ? Color.Lime : Color.Plum,
                        ShowOnHud = true,
                        IsContainerGPS = true
                    };

                    var gpsRef = gps;
                    long entity = 0L;
                    gpsCollection.SendAddGpsRequest(identityId, ref gpsRef, entity, false);
                    idx++;

                    // avoid spamming thousands of points; cap if needed
                    if (idx > 2000)
                        return;
                }
            }
        }



        public static void CalculateCapturableVolumes()
        {
            if (capturableVolumeTotal > 0)
                return;

            foreach (var planet in MyPlanets.GetPlanets())
            {
                if (uncapturablePlanets.Contains(GetBasePlanetName(planet)))
                    continue;
                // same inner/outer radii you use for point generation
                double innerR = planet.AverageRadius;
                double outerR = planet.MaximumRadius * Math.Pow((planet.Generator.SurfaceGravity / 0.05), 1.0 / 7.0);

                double innerR3 = Math.Pow(innerR, 3);
                double outerR3 = Math.Pow(outerR, 3);

                // volume of spherical shell: 4/3 π (R^3 - r^3)
                double shellVolume = (4.0 / 3.0) * Math.PI * (outerR3 - innerR3);

                capturableVolumes[planet] = shellVolume;
                capturableVolumeTotal += shellVolume;

                Log.Info($"CapturedResults: capturableVolume of '{GetBasePlanetName(planet)}': {capturableVolumes[planet]}");
            }

            foreach (var kvp in capturableVolumes)
            {
                Log.Info($"CapturedResults: capturableVolume %%% of '{GetBasePlanetName(kvp.Key)}': {kvp.Value / capturableVolumeTotal * 100:F2}% ");
            }
        }


        public static int CalculatePlayerActivity(long playerId, out double hours_2w)
        {
            hours_2w = 0;
            var storage = Plugin.Instance?._scores;
            if (storage == null)
            { if (Plugin.debug) Log.Info($"CalculatePlayerActivity: storage == null for '{playerId}'"); return 0;}

            if (!storage.Players.TryGetValue(playerId, out PlayerScoreInfo info))
            { if (Plugin.debug) Log.Info($"CalculatePlayerActivity: !storage.Players.TryGetValue(playerId, out PlayerScoreInfo info)");return 0;}

            if (!_recentActivity.TryGetValue(playerId, out var records) || records == null || records.Count == 0)
                return 0;

            var now = DateTime.UtcNow;
            var twoWeeksAgo = now.AddDays(-14);

            // Sum hours from records within last 2 weeks
            double recentHours = records
                .Where(r => r.Timestamp >= twoWeeksAgo)
                .Sum(r => r.Hours);


            int score = (int)(17.1 * Math.Pow(recentHours, 0.333333));
            hours_2w = recentHours;


            if (Plugin.debug) Log.Info($"CalculatePlayerActivity: id={playerId}, records={string.Join(", ", records.Select(a => a.ToString()))}, records.Count={records.Count}, recentHours={recentHours:0.000}h, Activity Score: {score}");
            return score;
        }


        public static void TickActivity()
        {
            var now = DateTime.UtcNow;
            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);

            foreach (var p in players)
            {
                long playerId = p.IdentityId;
                if (playerId <= 0)
                    continue;

                if (p.IsBot || MySession.Static.Players.IdentityIsNpc(playerId))
                    continue;

                if (!_lastSeen.TryGetValue(playerId, out DateTime last))
                    last = now;

                var dt = now - last;

                // ignore bad gaps / server stalls
                if (dt <= TimeSpan.Zero || dt > TimeSpan.FromMinutes(5))
                {
                    _lastSeen[playerId] = now; // resync baseline
                    continue;
                }

                // here you plug in whatever AFK heuristic you prefer
                if (IsPlayerAfk(p))
                    continue;           // do NOT move _lastSeen; "last seen" remains before AFK

                // count as active
                if (!_activeTime.TryGetValue(playerId, out TimeSpan t))
                    t = TimeSpan.Zero;

                t += dt;
                _activeTime[playerId] = t;
                _lastSeen[playerId] = now;    // update only when considered active

                // NEW: Update recent activity records (daily aggregation)
                UpdateRecentActivity(playerId, dt.TotalHours, now);

                // reflect into persisted scores
                var storage = Plugin.Instance?._scores;
                if (storage == null)
                    continue;

                if (!storage.Players.TryGetValue(playerId, out PlayerScoreInfo info))
                {
                    info = new PlayerScoreInfo
                    {
                        IdentityId = playerId,
                        PlayerName = p.DisplayName,
                        SteamId = p.SteamUserId,
                        Score = 0,
                        JoinedUtc = DateTime.UtcNow
                    };
                    storage.Players[playerId] = info;
                }

                info.ActivityHours = t.TotalHours;
                info.LastActivityUpdateUtc = now;
            }
        }

        private static void UpdateRecentActivity(long playerId, double deltaHours, DateTime now)
        {
            if (!_recentActivity.TryGetValue(playerId, out var records))
            {
                records = new List<ActivityRecord>();
                _recentActivity[playerId] = records;
            }

            // Clean old records (>2 weeks)
            var twoWeeksAgo = now.AddDays(-14);
            records.RemoveAll(r => r.Timestamp < twoWeeksAgo);

            // Get today's record
            var today = now.Date;
            var todayRecord = records.FirstOrDefault(r => r.Timestamp.Date == today);

            if (todayRecord != null)
            {
                // Update existing daily record (cap at 24h)
                todayRecord.Hours = Math.Min(todayRecord.Hours + deltaHours, 24.0);
            }
            else
            {
                // Add new daily record
                records.Add(new ActivityRecord
                {
                    Timestamp = today,
                    Hours = Math.Min(deltaHours, 24.0)
                });
            }

            // Keep only last 15 days of records (safety buffer)
            records.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            if (records.Count > 15)
                records.RemoveRange(15, records.Count - 15);
        }


        // NEW: Cleanup old activity data periodically
        public static void CleanupOldActivity()
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-15); // Slightly more than 2 weeks

            foreach (var kvp in _recentActivity.ToList())
            {
                var playerId = kvp.Key;
                var records = kvp.Value;

                records.RemoveAll(r => r.Timestamp < cutoff);

                if (records.Count == 0)
                {
                    _recentActivity.Remove(playerId);
                }
            }
        }


        public static void RegisterMovement(IMyCharacter character)
        {
            var id = character.ControllerInfo?.ControllingIdentityId ?? 0;
            if (id <= 0) return;
            _lastMovement[id] = DateTime.UtcNow;
        }

        static bool IsPlayerAfk(IMyPlayer p)
        {
            var id = p.IdentityId;
            if (!_lastMovement.TryGetValue(id, out var lastMove))
                return true; // never moved → treat as AFK

            return (DateTime.UtcNow - lastMove) > TimeSpan.FromMinutes(5);
        }

        public static void TickMovement()
        {
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCharacter);

            foreach (var e in entities)
            {
                var ch = (IMyCharacter)e;
                if (ch.Physics == null) continue;

                if (ch.Physics.LinearVelocity.LengthSquared() > 0.01f ||
                    ch.Physics.AngularVelocity.LengthSquared() > 0.01f)
                {
                    RegisterMovement(ch);
                }
            }
        }


        static string GetBasePlanetName(MyPlanet planet)
        {
            var storageName = planet.StorageName ?? string.Empty;

            int dashIndex = storageName.IndexOf('-');
            if (dashIndex <= 0)
                return storageName;

            return storageName.Substring(0, dashIndex) ;
        }
    }
}
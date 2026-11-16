#define USE_HARMONY

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using HarmonyLib;
using Newtonsoft.Json;
using ObjectBuilders.SafeZone;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace TorchPlugin
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class Plugin : TorchPluginBase, IWpfPlugin, ICommonPlugin
    {
        public const string PluginName = "Global Mechanics plugin";
        public static Plugin Instance { get; private set; }

        public long Tick { get; private set; }

        public IPluginLogger Log => Logger;
        private static readonly IPluginLogger Logger = new PluginLogger(PluginName);

        public IPluginConfig Config => config?.Data;
        private PersistentConfig<PluginConfig> config;
        private static readonly string ConfigFileName = $"{PluginName}.cfg";
        private readonly ConcurrentQueue<MySlimBlock> _pendingBlockChecks = new ConcurrentQueue<MySlimBlock>();
        public UserControl GetControl() => control ?? (control = new ConfigView());
        private ConfigView control;

        private TorchSessionManager sessionManager;
        bool _handlersAttached;

        private bool initialized;
        private bool failed;
        //private int passed_5min = 0;
        public static bool debug = false;


        static readonly MyDefinitionId GlobalRadarDef = new MyDefinitionId(typeof(MyObjectBuilder_Beacon), "LG_D2A_R");
        //public static readonly HashSet<long> FactionsWithGlobalRadar = new HashSet<long>();
        public static readonly Dictionary<long, List<MyFunctionalBlock>> IdentityRadars = new Dictionary<long, List<MyFunctionalBlock>>();
        DateTime _serverStartTime = DateTime.Now;

        static readonly Dictionary<int, string> sizeMapping = new Dictionary<int, string>
        {
            [500] = "Colossal",
            [250] = "Huge",
            [100] = "Large",
            [50] = "Medium",
            [25] = "Small",
            [0] = "Tiny",
        };

        public string GpsDescriptionGlobalSignals { get { return Config.GpsDescriptionString; } }
        public bool UseConnectedGrids { get { return Config.UseConnectedGrids; } }
        //public List<KeyValuePair<MyCubeGrid, DateTime>> gridsDetectable = new List<KeyValuePair<MyCubeGrid, DateTime>>();
        public List<MyCubeGrid> gridsDetectable = new List<MyCubeGrid>();
        public DateTime lastFindTime = DateTime.Now;


        // Faction Scores
        public DateTime launchTime = DateTime.Now;
        //public Dictionary<long, TorchPlayerInfo> Scores = new Dictionary<long, TorchPlayerInfo>();
        public ScoreStorage _scores = new ScoreStorage();
        private static Dictionary<long, int> oldPlayerScores = new Dictionary<long, int>();

        private static readonly Dictionary<MyDefinitionId, double> WeaponDps = new Dictionary<MyDefinitionId, double>(MyDefinitionId.Comparer);


        // ReSharper disable once UnusedMember.Local
        private readonly Commands commands = new Commands();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public override void Init(ITorchBase torch)
        {
            base.Init(torch);

            WeaponDps.Clear();
#if DEBUG
            // Allow the debugger some time to connect once the plugin assembly is loaded
            Thread.Sleep(100);
#endif

            Instance = this;

            Log.Info("Init");
            FactionScoreManager.Init(Log);

            var configPath = Path.Combine(StoragePath, ConfigFileName);
            config = PersistentConfig<PluginConfig>.Load(Log, configPath);

            //// Faction Scores
            //var configFileFS = Path.Combine(StoragePath, "playerScores.json");
            //if (File.Exists(configFileFS))
            //    Scores = JsonConvert.DeserializeObject<Dictionary<long, TorchPlayerInfo>>(File.ReadAllText(configFileFS)) ?? new Dictionary<long, TorchPlayerInfo>();


            // Faction Scores
            TorchPlayersCache.Load(StoragePath);
            LoadScores(StoragePath);

            // RestoreActivityStateFromScores
            foreach (var kv in _scores.Players)
            {
                var playerId = kv.Key;
                var playerInfo = kv.Value;

                if (playerInfo.ActivityHours > 0)
                    FactionScoreManager._activeTime[playerId] = TimeSpan.FromHours(playerInfo.ActivityHours);

                if (playerInfo.ActivityHours2Weeks > 0)
                    FactionScoreManager._activeTime2Weeks[playerId] = TimeSpan.FromHours(playerInfo.ActivityHours2Weeks);

                if (playerInfo.LastActivityUpdateUtc != default)
                    FactionScoreManager._lastSeen[playerId] = playerInfo.LastActivityUpdateUtc;


                if (playerInfo.RecentActivityRecords == null || playerInfo.RecentActivityRecords.Count == 0)
                {
                    if (playerInfo.ActivityHours2Weeks > 0)
                    {
                        playerInfo.RecentActivityRecords = new List<FactionScoreManager.ActivityRecord>
                        {
                            new FactionScoreManager.ActivityRecord
                            {
                                Timestamp = DateTime.UtcNow.Date,
                                Hours = Math.Min(playerInfo.ActivityHours2Weeks, 24.0)
                            }
                        };
                        Log.Info($"Migrated legacy ActivityHours2Weeks={playerInfo.ActivityHours2Weeks} for player {playerId}");
                    }
                    else
                    {
                        playerInfo.RecentActivityRecords = new List<FactionScoreManager.ActivityRecord>();
                    }
                }
                else
                {
                    FactionScoreManager._recentActivity[playerId] = playerInfo.RecentActivityRecords;
                }
            }
            //


            var gameVersionNumber = MyPerGameSettings.BasicGameInfo.GameVersion ?? 0;
            var gameVersion = new StringBuilder(MyBuildNumbers.ConvertBuildNumberFromIntToString(gameVersionNumber)).ToString();
            Common.SetPlugin(this, gameVersion, StoragePath);

#if USE_HARMONY
            var harmony = new Harmony(Name);
            if (!PatchHelpers.HarmonyPatchAll(Log, harmony))
            {
                failed = true;
                return;
            }
            //ConnectorUndockDiagnostics.Install(harmony);
#endif

            sessionManager = torch.Managers.GetManager<TorchSessionManager>();
            sessionManager.SessionStateChanged += SessionStateChanged;

            initialized = true;
        }

        public static void RegisterRadar(MyFunctionalBlock block)
        {
            Instance?.Log.Info($"RegisterRadar start: owner={block.OwnerId}, entity={block.EntityId}, working={block.IsWorking}");
            long ownerId = block.OwnerId;
            if (ownerId <= 0)
                return;

            lock (_radarLock)
            {
                if (!IdentityRadars.TryGetValue(ownerId, out var list))
                {
                    list = new List<MyFunctionalBlock>();
                    IdentityRadars[ownerId] = list;
                }

                Instance?.Log.Info($"RegisterRadar mid");
                if (!list.Contains(block))
                    list.Add(block);
                Instance?.Log.Info($"RegisterRadar end");
            }
        }

        public static void UnregisterRadar(MyFunctionalBlock block)
        {
            long ownerId = block.OwnerId;
            if (ownerId <= 0)
                return;

            lock (_radarLock)
            {
                if (!IdentityRadars.TryGetValue(ownerId, out var list))
                    return;

                list.Remove(block);
                if (list.Count == 0)
                    IdentityRadars.Remove(ownerId);
            }
        }

        public static void InitIdentityRadars()
        {
            lock (_radarLock)
            {
                IdentityRadars.Clear();
            }

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is MyCubeGrid);

            foreach (var e in entities)
            {
                var grid = (MyCubeGrid)e;
                if (grid.IsPreview || grid.Physics == null || grid.Closed || grid.MarkedForClose)
                    continue;

                grid.OnBlockOwnershipChanged -= GridOwnershipChanged;
                grid.OnBlockOwnershipChanged += GridOwnershipChanged;

                var fatBlocks = grid.GetFatBlocks();
                foreach (var fb in fatBlocks)
                {
                    var func = fb as MyFunctionalBlock;
                    if (func == null)
                        continue;

                    if (func.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
                        continue;

                    RegisterRadar(func);
                    Instance?.Log.Info("InitIdentityRadars: new radar registered");

                    func.IsWorkingChanged -= OnRadarIsWorkingChanged;
                    func.IsWorkingChanged += OnRadarIsWorkingChanged;

                    func.OnClose -= RadarBlockClosed;
                    func.OnClose += RadarBlockClosed;
                }
            }
        }
        public static void GridOwnershipChanged(MyCubeGrid grid)
        {
            if (grid == null || grid.Closed || grid.MarkedForClose)
                return;

            //Instance?.Log.Info($"GridOwnershipChanged: {grid.DisplayName}");

            foreach (var fb in grid.GetFatBlocks())
            {
                var func = fb as MyFunctionalBlock;
                if (func == null)
                    continue;

                if (func.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
                    continue;

                RadarWorkingChanged(func, skipIfUnchanged: true);
            }
        }

        private void SessionStateChanged(ITorchSession session, TorchSessionState newstate)
        {
            switch (newstate)
            {
                case TorchSessionState.Loading:
                    Log.Debug("Loading");
                    break;

                case TorchSessionState.Loaded:
                    Log.Debug("Loaded");
                    _serverStartTime = DateTime.Now;
                    FactionScoreManager.CalculateCapturablePlanetValues();
                    InitIdentityRadars();
                    _gridFirstSeen.Clear();
                    _gridLastPos.Clear();
                    _gridsSeeded = false;
                    //AttachHandlers();
                    break;

                case TorchSessionState.Unloading:
                    Log.Debug("Unloading");
                    if (initialized)
                    {
                        Log.Info("Unloading scores data into JSON");
                        SyncRecentActivityToScores();
                        SaveScores(StoragePath);
                        DetachHandlers();
                    }
                    break;

                case TorchSessionState.Unloaded:
                    Log.Debug("Unloaded");
                    break;
            }
        }

        public static void RadarBlockClosed(MyEntity entity)
        {
            var func = entity as MyFunctionalBlock;
            if (func == null || func.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
                return;

            Instance?.Log.Info("RadarBlockClosed");
            RadarWorkingChanged(func);
        }

        private static readonly object _radarLock = new object();
        private static readonly Dictionary<long, long> _lastRadarOwner = new Dictionary<long, long>();

        // Global Radar: old/new signal state keyed by radar-owner (faction id / owner identity).
        private static readonly Dictionary<long, Dictionary<long, DateTime>> _gridFirstSeen = new Dictionary<long, Dictionary<long, DateTime>>();
        private static readonly Dictionary<long, Dictionary<long, Vector3D>> _gridLastPos = new Dictionary<long, Dictionary<long, Vector3D>>();
        private static bool _gridsSeeded = false;
        private const ushort RadarSoundChannel = 42032;
        public static void RadarWorkingChanged(IMyCubeBlock block, bool skipIfUnchanged = false)
        {
            var func = block as MyFunctionalBlock;
            if (func == null || func.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
                return;

            long ownerId = func.OwnerId;

            lock (_radarLock)
            {
                long prevOwner;
                bool hadPrevOwner = _lastRadarOwner.TryGetValue(func.EntityId, out prevOwner);
                bool ownerChanged = !hadPrevOwner || prevOwner != ownerId;
                _lastRadarOwner[func.EntityId] = ownerId;

                if (skipIfUnchanged && !ownerChanged && !func.Closed && !func.MarkedForClose)
                    return;

                foreach (var kv in IdentityRadars.ToList())
                {
                    var list = kv.Value;
                    if (list == null)
                        continue;

                    if (list.Remove(func) && list.Count == 0)
                        IdentityRadars.Remove(kv.Key);
                }

                Instance?.Log.Info($"RadarWorkingChanged: grid='{func.CubeGrid?.DisplayName}' entity={func.EntityId}, " +
                    $"Enabled={func.Enabled}, Functional={func.IsFunctional}, IsWorking={func.IsWorking}, " +
                    $"Powered={func.ResourceSink?.IsPowered}, Closed={func.Closed}, MarkedForClose={func.MarkedForClose}, " +
                    $"OwnerId={ownerId}, ownerChanged={ownerChanged}");

                if (func.Closed || func.MarkedForClose || ownerId <= 0)
                    return;

                if (!IdentityRadars.TryGetValue(ownerId, out var ownerList))
                {
                    ownerList = new List<MyFunctionalBlock>();
                    IdentityRadars[ownerId] = ownerList;
                }

                if (!ownerList.Contains(func))
                    ownerList.Add(func);

                Instance?.Log.Info($"Radar update: Owner={ownerId}, total owners={IdentityRadars.Count}, " +
                    $"radars for owner={ownerList.Count}");
            }
        }

        public static void OnRadarIsWorkingChanged(IMyCubeBlock block)
        {
            RadarWorkingChanged(block, skipIfUnchanged: true);
        }




        public override void Dispose()
        {
            if (initialized)
            {
                Log.Debug("Disposing");

                sessionManager.SessionStateChanged -= SessionStateChanged;
                sessionManager = null;

                Log.Debug("Disposed");
            }

            Instance = null;

            base.Dispose();
        }

        public override void Update()
        {
            if (failed)
                return;

            try
            {
                CustomUpdate();
                Tick++;
            }
            catch (Exception e)
            {
                Log.Critical(e, "Update failed");
                failed = true;
            }
        }

        public bool AnyPlayerOnline()
        {
            var online = MySession.Static?.Players?.GetOnlinePlayers();
            //Log.Info($"AnyPlayerOnline: {online != null && online.Count > 0}");
            return online != null && online.Count > 0;
        }

        private void ProcessPendingBlockChecks()
        {
            // Ограничиваем работу за кадр, чтобы не лагать на массовых вставках.
            const int maxPerFrame = 500;
            int processed = 0;

            while (processed < maxPerFrame && _pendingBlockChecks.TryDequeue(out var slim))
            {
                processed++;
                try
                {
                    if (slim == null) continue;

                    var grid = slim.CubeGrid as MyCubeGrid;
                    if (grid == null) continue;
                    if (grid.Closed || grid.MarkedForClose) continue;
                    if (grid.IsNpcSpawnedGrid) continue;
                    if (grid.Physics == null) continue;
                    if (!grid.InScene) continue;      // грид ещё не в сцене — пропускаем
                    if (!grid.IsStatic) continue;     // не статик — подземные проверки неактуальны
                    if (slim.IsDestroyed) continue;

                    TryCheckBlockOnStaticGrid(slim);
                }
                catch (Exception e)
                {
                    Log.Error(e, "NoUndergroundBlocks: pending block check failed");
                }
            }
        }

        private void CustomUpdate()
        {
            // TODO: Put your update processing here. It is called on every simulation frame!
            if (initialized)
            {
                if (Config.GlobalSignals)
                {
                    if ((DateTime.Now - lastFindTime > TimeSpan.FromSeconds(10)) && (DateTime.Now - _serverStartTime > TimeSpan.FromSeconds(60)))
                    {
                        FindGrids(Config.UseConnectedGrids);
                        ShowGrids();
                        ShowRadarBeacons();
                        lastFindTime = DateTime.Now;
                        //Log.Info("Update signals");
                    }
                }

                FactionScoreManager.TickActivity();
                FactionScoreManager.TickMovement();
                //if (Config.FactionScore)
                //{
                //    // current UTC time
                //    var utcNow = DateTime.UtcNow;

                //    // "Local" time in UTC+3 (no DST assumed)
                //    var mskNow = utcNow + TimeSpan.FromHours(3);

                //    // Today 21:00 in UTC+3
                //    var mskNine = mskNow.Date.AddHours(21);

                //    // Corresponding UTC moment
                //    var utcNine = mskNine - TimeSpan.FromHours(3);

                //    //bool shouldRunDaily = utcNow >= utcNine && lastScoreTime < utcNine;   // 21:00 UTC+3 once per day

                //    //bool shouldRunRestart = (utcNow - lastScoreTime > TimeSpan.FromHours(1)) || (passed_5min == 1); // 5 minutes after restart
                //    bool shouldRunRestart = (passed_5min == 1); // 5 minutes after restart


                //    //if (/*AnyPlayerOnline() &&*/ (shouldRunDaily || shouldRunRestart))
                //    if (/*AnyPlayerOnline() &&*/ shouldRunRestart)
                //    {
                //        passed_5min = 2;
                //        launchTime = DateTime.UtcNow;

                //        StringBuilder resultSB = new StringBuilder();
                //        //Log.Info($"shouldRunDaily: {shouldRunDaily}, shouldRunRestart: {shouldRunRestart}, lastScoreTime: {lastScoreTime}, utcNow: {utcNow}");
                //        Log.Info($"shouldRunRestart: {shouldRunRestart}, lastScoreTime: {launchTime}, utcNow: {utcNow}");
                //        resultSB = UpdateFactionsList(resultSB);
                //        resultSB = ScoreFactions(resultSB);
                //        MyVisualScriptLogicProvider.SendChatMessage(resultSB.ToString());
                //    }
                //}
                //if ((passed_5min == 0) && (DateTime.UtcNow - launchTime > TimeSpan.FromMinutes(5)))
                //    passed_5min = 1;

                ProcessPendingBlockChecks();
                GridCleanupManager.Tick();
            }
            PatchHelpers.PatchUpdates();
        }

        public void SendTerritoryDebugGpsToAsker(bool sendRemove, long playerId = 0)
        {
            if (playerId != 0)
            {
                if (sendRemove)
                {
                    FactionScoreManager.SendDebugGpsToPlayer(playerId, "");
                    if (debug) Log.Info($"SendTerritoryDebugGpsToAsker: Sent to asker '{playerId}'");
                }
                else
                {
                    FactionScoreManager.RemoveDebugGpsFromPlayer(playerId);
                    if (debug) Log.Info($"SendTerritoryDebugGpsToAsker: Removed from asker '{playerId}'");
                }
            }
            else
            {
                Log.Info("SendTerritoryDebugGpsToAsker: playerId == 0");
                return;
            }
        }

        public bool IsNpcFaction(IMyFaction faction)
        {
            if (faction == null)
                return true;

            // 1) Все члены — NPC
            if (faction.IsEveryoneNpc())
                return true;

            // 2) Технические фракции
            if (faction.Tag == "SPID")
                return true;

            // 3) Основатель — NPC, если его имя заканчивается на "CEO"
            var founderIdentity = MySession.Static?.Players?.TryGetIdentity(faction.FounderId);
            string founderName = founderIdentity?.DisplayName
                                 ?? Plugin.GetPlayerNameByIdentity(faction.FounderId);

            if (founderName != null &&
                founderName.EndsWith("CEO", StringComparison.OrdinalIgnoreCase))
            {
                if (Plugin.debug)
                    Log.Info($"IsNpcFaction: '{faction.Name}' [{faction.Tag}] founder='{founderName}' ends with 'CEO' → NPC");
                return true;
            }

            return false;
        }


        public StringBuilder UpdateFactionsList(StringBuilder resultSB)
        {
            if (MySession.Static?.Factions?.Factions == null)
                return resultSB;

            // 1) Уборка: удаляем из _scores.Factions всё, что больше не существует
            //    или не проходит фильтр (NPC / технические / с основателем-CEO).
            //    Это закрывает TODO "check if faction deleted" и подчищает
            //    записи, осевшие в Scores.json до появления текущих проверок.
            var toRemove = new List<long>();
            foreach (var kv in _scores.Factions)
            {
                var fac = MySession.Static.Factions.TryGetFactionById(kv.Key);
                if (fac == null || IsNpcFaction(fac))
                    toRemove.Add(kv.Key);
            }
            foreach (var id in toRemove)
            {
                Log.Info($"UpdateFactionsList: removing faction {id} from scores (deleted/NPC/technical)");
                _scores.Factions.Remove(id);
            }

            // 2) Добавляем новые подходящие фракции
            foreach (var faction in MySession.Static.Factions.Factions)
            {
                if (IsNpcFaction(faction.Value))
                    continue;

                if (!_scores.Factions.TryGetValue(faction.Key, out var f))
                {
                    f = new FactionScoreInfo
                    {
                        FactionId = faction.Key,
                        Name = faction.Value.Name,
                        Tag = faction.Value.Tag,
                        Score = 0,
                        Power = 0,
                        TerritoryControl = 0f,
                        Activity = 0,
                        Hours2W = 0f,
                        CapturedPlanets = new List<string>(),
                        CreatedUtc = DateTime.UtcNow
                    };
                    _scores.Factions.Add(faction.Key, f);

                    Log.Info($"UpdateFactionsList: added new faction '{f.Name}' [{f.Tag}] ({faction.Key})");
                }
            }

            return resultSB;
        }

        public StringBuilder ScoreFactions(StringBuilder resultSB, DateTime previousScoreTimeUtc)
        {
            if (MySession.Static?.Factions?.Factions == null)
                return resultSB;

            int count = 0;
            oldPlayerScores.Clear();
            FactionScoreManager.factionPlanetCoveredFraction.Clear();

            resultSB.AppendLine("----- Faction Scores -----");

            if (previousScoreTimeUtc == DateTime.MinValue)
            {
                resultSB.AppendLine("Previous scoring: N/A (first run)");
            }
            else
            {
                var elapsed = DateTime.UtcNow - previousScoreTimeUtc;
                resultSB.AppendLine($"Previous scoring: {elapsed.TotalHours:0.0}h ago.");
            }

            var factionScoresKeys = _scores.Factions.Keys.ToList();  // Snapshot copy

            // Сюда собираем результаты по каждой фракции: (score, текст_вывода)
            var factionOutputs = new List<KeyValuePair<int, StringBuilder>>();

            foreach (var factionId in factionScoresKeys)
            {
                List<String> factionPlayersList = new List<string>();
                string factionPlayers;

                IMyFaction faction = MySession.Static.Factions.TryGetFactionById(factionId);
                if (faction == null) continue;

                bool factionActive = IsFactionActiveInLastWeek(faction);

                Log.Info($"ScoreFactions: Scoring faction {faction.Name}");

                int factionScore = 0, factionPower = 0, factionActivity = 0;
                int factionScoreDelta, factionPowerDelta, factionActivityDelta;
                float factionTerritoryDelta;
                double factionHours2WDelta;

                float factionTerritory = 1;
                double factionHours2W = 0;
                string factionResultText;

                foreach (var kvp_factionMember in faction.Members)
                {
                    long playerId = kvp_factionMember.Key;
                    int playerScore;
                    string playerName;
                    ulong playerSteamId;
                    string status;
                    int scoreDelta;

                    IMyPlayer player = MyAPIGateway.Players.TryGetIdentityId(playerId);
                    if (player != null)
                    {
                        playerName = player.DisplayName;
                        playerSteamId = player.SteamUserId;
                        status = "online";
                    }
                    else
                    {
                        playerName = GetPlayerNameByIdentity(playerId);
                        playerSteamId = GetPlayerSteamIdByIdentity(playerId) ?? 0;
                        status = "offline";
                    }

                    int power = FactionScoreManager.CalculatePlayerPower(playerId);
                    float territory = FactionScoreManager.CalculatePlayerTerritoryControl(playerId);
                    double hours_2w = 0;
                    int activity = FactionScoreManager.CalculatePlayerActivity(playerId, out hours_2w);
                    //activity /= faction.Members.Count;

                    factionPlayersList.Add($"{playerName} [{hours_2w:0.00}h]");

                    playerScore = (int)(power * (territory + 1) * activity);

                    factionScore += playerScore;
                    factionPower += power;
                    factionActivity += activity;
                    factionHours2W += hours_2w;
                    factionTerritory += territory;

                    string resultText;
                    if (_scores.Players.TryGetValue(playerId, out var pInfo))
                    {
                        scoreDelta = playerScore - pInfo.Score;
                        resultText = $"Updated score for player '{playerName}' ({status}) = {playerScore} ({(scoreDelta >= 0 ? "+" : "")}{scoreDelta}): \n - Power: {power} \n - Territory Control: {territory} \n - Activity: {activity} ({hours_2w:0.000}h)";

                        Log.Info(resultText);

                        oldPlayerScores.Add(playerId, pInfo.Score);
                    }
                    else
                    {
                        resultText = $"New player '{playerName}' ({status}) score = {playerScore}: \nPower = {power} \nTerritory Control = {territory} \nActivity = {activity} ({hours_2w:0.000}h)";

                        Log.Info(resultText);
                    }

                    UpdatePlayerScore(playerId, playerName, playerSteamId, playerScore);
                }
                factionPlayers = string.Join(", ", factionPlayersList);

                // Локальный StringBuilder на одну фракцию — чтобы потом можно было отсортировать
                var localSB = new StringBuilder();

                if (_scores.Factions.TryGetValue(factionId, out var fInfo))
                {
                    factionScoreDelta = factionScore - fInfo.Score;
                    factionPowerDelta = factionPower - fInfo.Power;
                    factionTerritoryDelta = factionTerritory - fInfo.TerritoryControl;
                    factionActivityDelta = factionActivity - fInfo.Activity;
                    factionHours2WDelta = factionHours2W - fInfo.Hours2W;
                    factionResultText = $" {fInfo.Name} [{fInfo.Tag}]: {factionScore} ({(factionScoreDelta >= 0 ? "+" : "")}{factionScoreDelta})" +
                        $"\n - Power: {factionPower} ({(factionPowerDelta >= 0 ? "+" : "")}{factionPowerDelta})" +
                        $"\n - Territory Control: {factionTerritory} ({(factionTerritoryDelta >= 0 ? "+" : "")}{factionTerritoryDelta})" +
                        $"\n - Activity: {factionActivity} [{factionHours2W:0.00}h in 2 weeks]  ({(factionActivityDelta >= 0 ? "+" : "")}{factionActivityDelta} [{(factionHours2WDelta >= 0 ? "+" : "")}{factionHours2WDelta:0.00}h])" +
                        $"\nPlayers: {factionPlayers}.";

                    fInfo.Score = factionScore;
                    fInfo.Power = factionPower;
                    fInfo.TerritoryControl = factionTerritory;
                    fInfo.Activity = factionActivity;
                    fInfo.Hours2W = factionHours2W;
                }
                else
                {
                    _scores.Factions[factionId] = new FactionScoreInfo
                    {
                        FactionId = factionId,
                        Name = faction.Name,
                        Tag = faction.Tag,
                        Score = factionScore,
                        Power = factionPower,
                        TerritoryControl = factionTerritory,
                        Activity = factionActivity,
                        Hours2W = factionHours2W,
                        CapturedPlanets = new List<string>()
                    };

                    factionResultText = $"Faction score for NEW FACTION [{faction.Tag}]: = {factionScore}: \n - Power: {factionPower} \n - Territory Control: {factionTerritory} \n - Activity: {factionActivity} [{factionHours2W:0.00}h]";
                }

                Log.Info(factionResultText);
                localSB.AppendLine(factionResultText);

                // CapturedResults дописывает свои строки в локальный буфер
                localSB = FactionScoreManager.CapturedResults(factionId, localSB);
                localSB.AppendLine();

                // Запоминаем (score, текст) для последующей сортировки
                //factionOutputs.Add(new KeyValuePair<int, StringBuilder>(factionScore, localSB));

                if (_scores.Factions.TryGetValue(factionId, out var fInfoHist))
                {
                    fInfoHist.ScoreHistory.Add(new FactionScoreHistoryEntry
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Score = factionScore
                    });
                }

                if (factionActive)
                {
                    factionOutputs.Add(new KeyValuePair<int, StringBuilder>(factionScore, localSB));
                    count++;
                }
                else
                {
                    if (debug)
                        Log.Info($"ScoreFactions: faction '{faction.Name}' inactive >7d, hidden from output");
                }
            }

            // Вывод отсортированных по убыванию счёта фракций
            foreach (var entry in factionOutputs.OrderByDescending(kv => kv.Key))
                resultSB.Append(entry.Value);

            if (count > 0)
                resultSB.Append("Factions: ").Append(count);

            return resultSB;
        }

        public StringBuilder ShowFactionScores(StringBuilder resultSB, bool showAll = false)
        {
            if (MySession.Static?.Factions?.Factions == null)
                return resultSB;

            var count = 0;

            resultSB.AppendLine("----- Faction Scores -----");

            // Сортируем фракции по убыванию Score
            foreach (var fInfo in _scores.Factions.Values.OrderByDescending(f => f.Score))
            {
                if (!showAll && !IsFactionActiveInLastWeek(fInfo.FactionId))
                    continue;

                string factionPlayers = "";
                var faction = MySession.Static.Factions.TryGetFactionById(fInfo.FactionId);
                if (faction != null)
                {
                    var factionPlayersList = new List<string>();
                    foreach (var kvp_factionMember in faction.Members)
                    {
                        long playerId = kvp_factionMember.Key;
                        string playerName;

                        IMyPlayer player = MyAPIGateway.Players.TryGetIdentityId(playerId);
                        if (player != null)
                            playerName = player.DisplayName;
                        else
                            playerName = GetPlayerNameByIdentity(playerId) ?? $"Unknown({playerId})";

                        double hours2w = 0;
                        if (_scores.Players.TryGetValue(playerId, out var pInfo))
                            hours2w = pInfo.ActivityHours2Weeks;

                        factionPlayersList.Add($"{playerName} [{hours2w:0.00}h]");
                    }
                    factionPlayers = string.Join(", ", factionPlayersList);
                }

                resultSB.AppendLine($" {fInfo.Name} [{fInfo.Tag}]: {fInfo.Score}" +
            $"\n - Power: {fInfo.Power}" +
            $"\n - Territory Control: {fInfo.TerritoryControl}" +
            $"\n - Activity: {fInfo.Activity} [{fInfo.Hours2W:0.00}h]" +
            $"\nPlayers: {factionPlayers}\n");

                count++;
            }
            if (count > 0)
                resultSB.Append("Factions: ").Append(count);

            if (debug) Log.Info(resultSB.ToString());

            return resultSB;
        }


        void UpdatePlayerScore(long identityId, string name, ulong steamId, int newScore)
        {
            if (!_scores.Players.TryGetValue(identityId, out var p))
            {
                p = new PlayerScoreInfo
                {
                    IdentityId = identityId,
                    PlayerName = name,
                    SteamId = steamId,
                    Score = 0,
                    JoinedUtc = DateTime.UtcNow
                };
                _scores.Players[identityId] = p;
            }

            p.Score = newScore;
            p.PlayerName = name; // refresh
            p.SteamId = steamId;
        }

        void LoadScores(string instancePath)
        {
            var path = Path.Combine(instancePath, "Scores.json");
            if (!File.Exists(path))
            {
                _scores = new ScoreStorage();
                return;
            }

            var json = File.ReadAllText(path);
            _scores = JsonConvert.DeserializeObject<ScoreStorage>(json) ?? new ScoreStorage();
        }

        public static void SyncRecentActivityToScores()
        {
            var storage = Instance?._scores;
            if (storage == null)
                return;

            var now = DateTime.UtcNow;
            var twoWeeksAgo = now.AddDays(-14);

            foreach (var kv in FactionScoreManager._recentActivity)
            {
                long playerId = kv.Key;
                var records = kv.Value;

                if (!storage.Players.TryGetValue(playerId, out var info))
                    continue;

                double recentHours = records
                    .Where(r => r.Timestamp >= twoWeeksAgo)
                    .Sum(r => r.Hours);

                info.RecentActivityRecords = records;
                info.ActivityHours2Weeks = recentHours; // актуальное значение на момент сохранения
            }
        }
        public void SaveScores() => SaveScores(StoragePath);   // <-- добавлено

        void SaveScores(string instancePath)
        {
            var path = Path.Combine(instancePath, "Scores.json");
            var json = JsonConvert.SerializeObject(_scores, Formatting.Indented);
            File.WriteAllText(path, json);
        }


        public StringBuilder ShowPlayerScores(StringBuilder resultSB)
        {
            if (_scores == null || _scores.Players == null || _scores.Players.Count == 0)
                return resultSB;

            var sb = new StringBuilder();
            int scoreDelta = 0;

            foreach (var kvp in _scores.Players)
            {
                long identityId = kvp.Key;
                var info = kvp.Value;

                // Try to also show faction tag if available
                string factionTag = "";
                var faction = MySession.Static?.Factions?.TryGetPlayerFaction(identityId);
                if (faction != null)
                    factionTag = $"[{faction.Tag}] ";

                if (oldPlayerScores.ContainsKey(identityId))
                    scoreDelta = info.Score - oldPlayerScores[identityId];
                sb.AppendLine($"- {factionTag}{info.PlayerName ?? "Unknown"}: {info.Score}({(scoreDelta >= 0 ? "+" : "")}{scoreDelta})");
            }


            //MyVisualScriptLogicProvider.SendChatMessage($"PlayerScore: {sb}");
            Log.Info($"Player Score: \n{sb}");
            resultSB.AppendLine($"Player Score: \n{sb}");

            return resultSB;
        }


        public static string GetPlayerNameByIdentity(long identityId)
        {
            if (TorchPlayersCache.ByIdentity == null)
                return null;

            if (TorchPlayersCache.ByIdentity.TryGetValue(identityId, out var info))
                return info.Player;

            return null; // unknown identity
        }

        public static ulong? GetPlayerSteamIdByIdentity(long identityId)
        {
            if (TorchPlayersCache.ByIdentity == null)
                return null;

            if (TorchPlayersCache.ByIdentity.TryGetValue(identityId, out var info))
                return info.SteamID;

            return null; // unknown identity
        }


        private bool IsNPCOwned(IMyCubeGrid grid)
        {
            if (grid.BigOwners == null || grid.BigOwners.Count == 0)
                return false;

            long owner = grid.BigOwners[0];
            var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);

            if (faction != null)
            {
                bool isNpc = !faction.AcceptHumans || faction.IsEveryoneNpc();
                //Instance?.Log.Info($"IsNPCOwned faction: grid='{grid.DisplayName}', " +
                //    $"faction='{faction.Name}', AcceptHumans={faction.AcceptHumans}, " +
                //    $"IsEveryoneNpc={faction.IsEveryoneNpc()}, result={isNpc}");
                return isNpc;
            }

            var steamId = MySession.Static.Players.TryGetSteamId(owner);
            var identity = MySession.Static.Players.TryGetIdentity(owner);
            bool result = steamId == 0;
            //Instance?.Log.Info($"IsNPCOwned steamId: grid='{grid.DisplayName}', " +
            //    $"ownerId={owner}, identity='{identity?.DisplayName ?? "null"}', " +
            //    $"steamId={steamId}, result={result}");

            return result;
        }


        public void FindGrids(bool connected)
        {
            List<MyCubeGrid> gridsList = new List<MyCubeGrid>();

            if (connected)
            {
                foreach (var group in MyCubeGridGroups.Static.Physical.Groups)
                {
                    foreach (var node in group.Nodes)
                    {
                        MyCubeGrid cubeGrid = node.NodeData;
                        IMyCubeGrid grid = cubeGrid;

                        if (cubeGrid.IsNpcSpawnedGrid) continue;
                        if (IsNPCOwned(cubeGrid)) continue;
                        if (cubeGrid.Physics == null) continue;
                        if (cubeGrid.NaturalGravity.Length() > 0) continue;

                        //if (cubeGrid.BlocksCount < 20)
                        //    if (!cubeGrid.GetFatBlocks().Any(b => b is IMyPowerProducer p && p.IsWorking)) continue;

                        if (cubeGrid.BlocksCount < 20)
                            if (grid.ResourceDistributor.MaxAvailableResourceByType(MyResourceDistributorComponent.ElectricityId) <= 0) continue;
                        //Log.Info($"Detectable grid: '{cubeGrid.DisplayName}', " +
                        //$"NpcSpawned={cubeGrid.IsNpcSpawnedGrid}, " +
                        //$"BigOwners={cubeGrid.BigOwners?.Count ?? 0}, " +
                        //$"IsNPCOwned={IsNPCOwned(cubeGrid)}");

                        gridsList.Add(cubeGrid);
                    }
                }
            }
            else
            {
                foreach (var group in MyCubeGridGroups.Static.Mechanical.Groups)
                {
                    foreach (var node in group.Nodes)
                    {
                        MyCubeGrid cubeGrid = node.NodeData;
                        IMyCubeGrid grid = cubeGrid;

                        if (cubeGrid.IsNpcSpawnedGrid) continue;
                        if (IsNPCOwned(cubeGrid)) continue;
                        if (cubeGrid.Physics == null) continue;
                        if (cubeGrid.NaturalGravity.Length() > 0) continue;

                        //if (cubeGrid.BlocksCount < 20)
                        //    if (!cubeGrid.GetFatBlocks().Any(b => b is IMyPowerProducer p && p.IsWorking)) continue;

                        if (cubeGrid.BlocksCount < 20)
                            if (grid.ResourceDistributor.MaxAvailableResourceByType(MyResourceDistributorComponent.ElectricityId) <= 0) continue;

                        //Log.Info($"Detectable grid: '{cubeGrid.DisplayName}', " +
                        //$"NpcSpawned={cubeGrid.IsNpcSpawnedGrid}, " +
                        //$"BigOwners={cubeGrid.BigOwners?.Count ?? 0}, " +
                        //$"IsNPCOwned={IsNPCOwned(cubeGrid)}");

                        gridsList.Add(cubeGrid);
                    }
                }
            }

            gridsDetectable = gridsList;
            lastFindTime = DateTime.Now;
        }

        public void ShowGrids()
        {
            RemoveGpsFromAllPlayers();

            MyGpsCollection gpsCollection = (MyGpsCollection)MyAPIGateway.Session?.GPS;
            if (gpsCollection == null)
                return;

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                long identityId = player.Identity.IdentityId;

                // Собираем радары, которые разрешают этого игрока
                var allowingRadars = new List<IMyBeacon>();
                lock (_radarLock)
                {
                    foreach (var kv in IdentityRadars)
                    {
                        if (kv.Value == null) continue;
                        foreach (var radarBlock in kv.Value)
                        {
                            if (radarBlock == null || radarBlock.Closed || radarBlock.MarkedForClose) continue;
                            var radarBeacon = radarBlock as IMyBeacon;
                            if (radarBeacon == null || !radarBeacon.IsWorking) continue;
                            var hud = radarBeacon.HudText ?? string.Empty;
                            if (hud.IndexOf("arming", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                            if (ShouldShare(radarBeacon, identityId))
                                allowingRadars.Add(radarBeacon);
                        }
                    }
                }

                if (allowingRadars.Count == 0)
                    continue;

                // Используем первый разрешающий радар как владельца для окраски / затухания / звука.
                IMyBeacon ownerRadar = allowingRadars[0];
                long radarOwnerId = ownerRadar.OwnerId;
                IMyFaction radarOwnerFaction = MySession.Static.Factions?.TryGetPlayerFaction(radarOwnerId);
                long ownerKey = radarOwnerFaction != null ? radarOwnerFaction.FactionId : radarOwnerId;

                float alertRange = 100000f;
                bool noAlertSound = false;
                bool noHud = false;
                bool enemiesOnly = false;
                bool noStations = false;
                bool noShips = false;
                bool onlyPowered = false;
                bool onlyMovement = false;
                TryGetRadarSettings(ownerRadar, out alertRange, out noAlertSound, out noHud, out enemiesOnly, out noStations, out noShips, out onlyPowered, out onlyMovement);


                //Log.Info($"Global radar on grid '{ownerRadar.CubeGrid.DisplayName}': alertRange={alertRange}");

                Vector3D radarPos = ownerRadar.PositionComp.WorldAABB.Center;
                ulong steamId = MySession.Static.Players.TryGetSteamId(identityId);

                foreach (var grid in gridsDetectable)
                {
                    if (grid == null || grid.Closed || grid.MarkedForClose)
                        continue;

                    // The radar must not detect the grid it is mounted on.
                    if (ownerRadar.CubeGrid != null && grid.EntityId == ownerRadar.CubeGrid.EntityId)
                        continue;

                    // NoHud = 1 → no HUD markers (and no sound) from this radar.
                    if (noHud)
                        continue;

                    // Detection filters from CustomData.
                    if (noStations && grid.IsStatic)
                        continue;
                    if (noShips && !grid.IsStatic)
                        continue;
                    if (onlyPowered && ((IMyCubeGrid)grid).ResourceDistributor.MaxAvailableResourceByType(MyResourceDistributorComponent.ElectricityId) <= 0f)
                        continue;
                    if (enemiesOnly && !IsEnemyGrid(grid, radarOwnerId, radarOwnerFaction))
                        continue;

                    long targetId = grid.EntityId;
                    Vector3D position = grid.PositionComp.WorldAABB.Center;

                    // Friendly/Foe colour relative to the radar owner, with the mod's fallbacks.
                    Color baseColor = ComputeGridColor(grid, radarOwnerId, radarOwnerFaction);

                    bool isNew;
                    bool moved;
                    float brightness = UpdateSignalState(ownerKey, targetId, position, out isNew, out moved);

                    // OnlyMovement = 1 → show only first detection or when it moved.
                    if (onlyMovement && !isNew && !moved)
                        continue;

                    Color faded = DarkenColor(baseColor, brightness);

                    string gridName = MakeShowName(grid);
                    MyGps gps = CreateGpsColored(position, gridName, GpsDescriptionGlobalSignals, faded);
                    MyGps gpsRef = gps;
                    long entity = 0L;
                    gpsCollection.SendAddGpsRequest(identityId, ref gpsRef, entity, false);

                    //Log.Info($"Target: grid={grid.DisplayName}, distance={Vector3D.Distance(radarPos, position)}, baseColor={baseColor}, faded={faded}");

                    bool insideAlertRange = Vector3D.Distance(radarPos, position) <= alertRange;
                    SendRadarSound(steamId, targetId, isNew, moved, insideAlertRange, noAlertSound);
                }
            }

            _gridsSeeded = true;
        }

        private static void TryGetRadarSettings(IMyBeacon radar, out float alertRange, out bool noAlertSound, out bool noHud, out bool enemiesOnly, out bool noStations, out bool noShips, out bool onlyPowered, out bool onlyMovement)
        {
            alertRange = 100000f;
            noAlertSound = false;
            noHud = false;
            enemiesOnly = false;
            noStations = false;
            noShips = false;
            onlyPowered = false;
            onlyMovement = false;
            try
            {
                var gameLogic = radar?.GameLogic;
                if (gameLogic == null)
                    return;

                var logicType = gameLogic.GetType();
                if (logicType.FullName != "TerritoryBeaconBlock.RadarLogic")
                    return;

                var alertProp = logicType.GetProperty("AlertRange");
                if (alertProp != null)
                {
                    var v = alertProp.GetValue(gameLogic);
                    if (v is float) alertRange = (float)v;
                }

                noAlertSound = ReadBoolSetting(logicType, gameLogic, "NoAlertSound");
                noHud = ReadBoolSetting(logicType, gameLogic, "NoHud");
                enemiesOnly = ReadBoolSetting(logicType, gameLogic, "EnemiesOnly");
                noStations = ReadBoolSetting(logicType, gameLogic, "NoStations");
                noShips = ReadBoolSetting(logicType, gameLogic, "NoShips");
                onlyPowered = ReadBoolSetting(logicType, gameLogic, "OnlyPowered");
                onlyMovement = ReadBoolSetting(logicType, gameLogic, "OnlyMovement");
            }
            catch (Exception ex)
            {
                Instance?.Log.Error(ex, "GlobalRadarSound: read RadarLogic settings failed");
            }
        }

        private static bool ReadBoolSetting(Type logicType, object gameLogic, string name)
        {
            var prop = logicType.GetProperty(name);
            if (prop == null)
                return false;
            var v = prop.GetValue(gameLogic);
            if (v is bool)
                return (bool)v;
            return false;
        }

        private static Color ComputeGridColor(IMyCubeGrid grid, long radarOwnerId, IMyFaction radarOwnerFaction)
        {
            // Own grids + (if the owner has a faction) faction members' grids -> light-green.
            if (grid.BigOwners != null)
            {
                foreach (long owner in grid.BigOwners)
                {
                    if (owner == radarOwnerId)
                        return new Color(144, 238, 144);   // light-green
                    if (radarOwnerFaction != null && radarOwnerFaction.Members.ContainsKey(owner))
                        return new Color(144, 238, 144);   // light-green
                }
            }

            long gridOwnerId = grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            if (gridOwnerId == 0)
                return Color.Red;                           // unowned -> enemy
            if (radarOwnerFaction == null)
                return Color.Red;                           // radar owner without faction -> all non-own = enemy

            IMyFaction gridFaction = ResolveFaction(gridOwnerId);
            if (gridFaction == null)
                return Color.Red;                           // unresolvable -> enemy

            MyRelationsBetweenFactions relation = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(
                gridFaction.FactionId, radarOwnerFaction.FactionId);
            switch (relation)
            {
                case MyRelationsBetweenFactions.Enemies: return Color.Red;
                case MyRelationsBetweenFactions.Neutral: return Color.White;
                case MyRelationsBetweenFactions.Friends: return new Color(0, 128, 0);   // green
                case MyRelationsBetweenFactions.Allies: return Color.AliceBlue;
                default: return Color.Red;
            }
        }

        private static IMyFaction ResolveFaction(long identityId)
        {
            if (identityId == 0)
                return null;

            var direct = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identityId);
            if (direct != null)
                return direct;

            foreach (var kv in MyAPIGateway.Session.Factions.Factions)
            {
                var f = kv.Value;
                if (f != null && f.Members.ContainsKey(identityId))
                    return f;
            }
            return null;
        }

        private static bool IsEnemyGrid(IMyCubeGrid grid, long radarOwnerId, IMyFaction radarOwnerFaction)
        {
            // Own / faction member's grid -> not enemy.
            if (grid.BigOwners != null)
            {
                foreach (long owner in grid.BigOwners)
                {
                    if (owner == radarOwnerId)
                        return false;
                    if (radarOwnerFaction != null && radarOwnerFaction.Members.ContainsKey(owner))
                        return false;
                }
            }

            long gridOwnerId = grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            if (gridOwnerId == 0)
                return true;   // unowned -> enemy
            if (radarOwnerFaction == null)
                return true;   // owner without faction -> all non-own = enemy

            IMyFaction gridFaction = ResolveFaction(gridOwnerId);
            if (gridFaction == null)
                return true;   // unresolvable -> enemy

            MyRelationsBetweenFactions relation = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(
                gridFaction.FactionId, radarOwnerFaction.FactionId);
            if (relation == MyRelationsBetweenFactions.Enemies)
                return true;
            if (gridFaction.IsEveryoneNpc()
                && relation != MyRelationsBetweenFactions.Friends
                && relation != MyRelationsBetweenFactions.Allies)
                return true;   // NPC faction that isn't friend/ally -> enemy

            return false;   // Neutral / Friends / Allies (non-NPC) -> not enemy
        }

        private static float UpdateSignalState(long ownerKey, long targetId, Vector3D pos, out bool isNew, out bool moved)
        {
            DateTime now = DateTime.UtcNow;

            Dictionary<long, DateTime> perOwner;
            DateTime firstSeen;
            bool had;
            if (_gridFirstSeen.TryGetValue(ownerKey, out perOwner))
                had = perOwner.TryGetValue(targetId, out firstSeen);
            else
            {
                perOwner = new Dictionary<long, DateTime>();
                _gridFirstSeen[ownerKey] = perOwner;
                had = false;
                firstSeen = DateTime.MinValue;
            }

            isNew = false;
            if (!had)
            {
                if (_gridsSeeded)
                {
                    firstSeen = now;
                    isNew = true;
                }
                else
                    firstSeen = now.AddHours(-1.0);   // existing at server start -> old
                perOwner[targetId] = firstSeen;
            }

            Dictionary<long, Vector3D> posOwner;
            Vector3D lastPos;
            bool hadPos;
            if (_gridLastPos.TryGetValue(ownerKey, out posOwner))
                hadPos = posOwner.TryGetValue(targetId, out lastPos);
            else
            {
                posOwner = new Dictionary<long, Vector3D>();
                _gridLastPos[ownerKey] = posOwner;
                hadPos = false;
                lastPos = Vector3D.Zero;
            }

            moved = hadPos && Vector3D.DistanceSquared(lastPos, pos) >= 1.0;
            posOwner[targetId] = pos;

            double age = (now - firstSeen).TotalHours;
            if (age < 1.0)
                return 1.0f - 0.4f * (float)age;   // 100% -> 60%
            return moved ? 0.6f : 0.3f;
        }

        private static Color DarkenColor(Color c, float brightness)
        {
            return new Color((int)(c.R * brightness), (int)(c.G * brightness), (int)(c.B * brightness), c.A);
        }

        private static void SendRadarSound(ulong steamId, long targetId, bool isNew, bool moved, bool insideAlertRange, bool noSound)
        {
            if (steamId == 0)
                return;
            try
            {
                byte[] data = new byte[12];
                BitConverter.GetBytes(targetId).CopyTo(data, 0);
                data[8] = isNew ? (byte)1 : (byte)0;
                data[9] = moved ? (byte)1 : (byte)0;
                data[10] = insideAlertRange ? (byte)1 : (byte)0;
                data[11] = noSound ? (byte)1 : (byte)0;
                MyAPIGateway.Multiplayer.SendMessageTo(RadarSoundChannel, data, steamId, true);
            }
            catch (Exception ex)
            {
                Instance?.Log.Error(ex, "GlobalRadarSound: send failed");
            }
        }

        private static string MakeShowName(IMyCubeGrid grid)
        {
            string name = "";

            foreach (var _type in sizeMapping)
            {
                if (grid.PositionComp.WorldAABB.Size.AbsMax() >= _type.Key)
                {
                    name = _type.Value;
                    break;
                }
            }
            name += grid.IsStatic ? " Station" : " Ship";

            return name;
        }

        //public void ShowGrids()
        //{
        //    RemoveGpsFromAllPlayers();

        //    MyGpsCollection gpsCollection = (MyGpsCollection)MyAPIGateway.Session?.GPS;
        //    if (gpsCollection == null)
        //        return;

        //    // Проверяем: есть ли хоть один активный глобальный радар в мире
        //    // Если ни одного нет — никому ничего не показываем
        //    bool anyActiveRadar = IdentityRadars.Values
        //        .Any(list => list != null && list.Any(b =>
        //        {
        //            if (b == null || b.Closed || b.MarkedForClose) return false;
        //            var rb = b as IMyBeacon;
        //            if (rb == null || !rb.IsWorking) return false;
        //            var hud = rb.HudText ?? string.Empty;
        //            return hud.IndexOf("arming", StringComparison.OrdinalIgnoreCase) < 0;
        //        }));

        //    if (!anyActiveRadar)
        //        return;

        //    foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
        //    {
        //        long identityId = player.Identity.IdentityId;

        //        // Собираем "свои" identity: сам игрок + его фракция
        //        var ownIdentities = new HashSet<long> { identityId };
        //        var faction = MySession.Static.Factions?.TryGetPlayerFaction(identityId);
        //        if (faction != null)
        //            foreach (var memberId in faction.Members.Keys)
        //                ownIdentities.Add(memberId);

        //        foreach (var grid in gridsDetectable)
        //        {
        //            // Не показываем свои и фракционные гриды
        //            if (grid.BigOwners.Any(o => ownIdentities.Contains(o)))
        //                continue;

        //            Vector3D position = grid.PositionComp.WorldAABB.Center;
        //            MyGps gps = CreateGps(position, grid.DisplayName, GpsDescriptionGlobalSignals);
        //            MyGps gpsRef = gps;

        //            long entity = 0L;
        //            gpsCollection.SendAddGpsRequest(identityId, ref gpsRef, entity, false);
        //        }
        //    }
        //}

        public void RemoveGpsFromAllPlayers()
        {
            foreach (var identity in MySession.Static.Players.GetAllIdentities())
            {
                List<IMyGps> gpsList = MyAPIGateway.Session?.GPS.GetGpsList(identity.IdentityId);

                if (gpsList == null)
                    continue;

                foreach (IMyGps gps in gpsList)
                {

                    if (!(gps is MyGps myGps))
                        continue;

                    string desc = myGps.Description;

                    if (string.IsNullOrEmpty(desc))
                        continue;

                    if (!desc.Contains(GpsDescriptionGlobalSignals))
                        continue;

                    MyAPIGateway.Session.GPS.RemoveGps(identity.IdentityId, gps);
                    MyAPIGateway.Session.GPS.RemoveLocalGps(gps);
                }
            }
        }

        public static bool PlayerOrFactionHasActiveRadar(long identityId)
        {
            // 1) сначала проверяем личные радары игрока
            if (HasActiveRadarForIdentity(identityId))
                return true;

            // 2) если он во фракции — проверяем всех членов фракции
            var faction = MySession.Static.Factions?.TryGetPlayerFaction(identityId);
            if (faction == null)
                return false;

            foreach (var member in faction.Members.Keys) // member.Key = IdentityId
            {
                if (HasActiveRadarForIdentity(member))
                    return true;
            }

            return false;
        }

        static bool HasActiveRadarForIdentity(long identityId)
        {
            if (!IdentityRadars.TryGetValue(identityId, out var list) || list == null || list.Count == 0)
                return false;

            //Instance?.Log.Info($"HasActiveRadar: owner={identityId}, count={list.Count}");
            bool hasActive = false;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var b = list[i];
                if (b == null || b.Closed || b.MarkedForClose)
                {
                    list.RemoveAt(i);
                    continue;
                }

                var beacon = b as IMyBeacon;
                if (beacon == null)
                    continue;

                // активный, если работает и HudText НЕ содержит "arming"                
                var hud = beacon.HudText ?? string.Empty;
                if (beacon.IsWorking &&
                    hud.IndexOf("arming", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    hasActive = true;
                }
            }

            if (list.Count == 0)
                IdentityRadars.Remove(identityId);

            return hasActive;
        }



        // Возвращает true, если targetPlayerId должен видеть сигнал от ownerBeacon
        private bool ShouldShare(IMyBeacon ownerBeacon, long targetPlayerId)
        {
            var gameLogic = ownerBeacon?.GameLogic;
            if (gameLogic == null) return false;

            var logicType = gameLogic.GetType();
            if (logicType.FullName != "TerritoryBeaconBlock.RadarLogic")
                return false;

            var shareModeProp = logicType.GetProperty("ShareMode");
            if (shareModeProp == null) return false;
            int shareMode = (int)shareModeProp.GetValue(gameLogic);

            long radarOwnerId = ownerBeacon.OwnerId;

            switch (shareMode)
            {
                case 0: // OnlyMe
                    return targetPlayerId == radarOwnerId;

                case 1: // MyFaction
                    if (targetPlayerId == radarOwnerId) return true;
                    var ownerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(radarOwnerId);
                    var targetFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(targetPlayerId);
                    return ownerFaction != null && targetFaction != null
                        && ownerFaction.FactionId == targetFaction.FactionId;

                case 2: // Everyone
                    return true;

                case 3: // Factions
                    {
                        if (targetPlayerId == radarOwnerId) return true;
                        var tf = MyAPIGateway.Session.Factions.TryGetPlayerFaction(targetPlayerId);
                        if (tf == null) return false;
                        var prop = logicType.GetProperty("SharedFactionIds");
                        if (prop == null) return false;
                        var set = prop.GetValue(gameLogic) as IEnumerable<long>;
                        return set != null && set.Contains(tf.FactionId);
                    }

                case 4: // Players
                    {
                        if (targetPlayerId == radarOwnerId) return true;
                        var prop = logicType.GetProperty("SharedPlayerIds");
                        if (prop == null) return false;
                        var set = prop.GetValue(gameLogic) as IEnumerable<long>;
                        return set != null && set.Contains(targetPlayerId);
                    }

                default:
                    return false;
            }
        }

        public string GpsDescriptionRadarSignal { get { return "GlobalRadarSignal"; } }
        private const float RadarVisibilityRange = 500000f; // 500km

        public void RemoveRadarGpsFromAllPlayers()
        {
            foreach (var identity in MySession.Static.Players.GetAllIdentities())
            {
                var gpsList = MyAPIGateway.Session?.GPS.GetGpsList(identity.IdentityId);
                if (gpsList == null) continue;

                foreach (var gps in gpsList)
                {
                    if (!(gps is MyGps myGps)) continue;
                    if (myGps.Description == null) continue;
                    if (!myGps.Description.Contains(GpsDescriptionRadarSignal)) continue;
                    MyAPIGateway.Session?.GPS.RemoveGps(identity.IdentityId, gps);
                }
            }
        }

        public void ShowRadarBeacons()
        {
            RemoveRadarGpsFromAllPlayers();

            var gpsCollection = (MyGpsCollection)MyAPIGateway.Session?.GPS;
            if (gpsCollection == null)
                return;

            // собираем все активные радары
            var activeRadars = new List<MyFunctionalBlock>();
            foreach (var kv in IdentityRadars)
            {
                foreach (var b in kv.Value)
                {
                    if (b == null || b.Closed || b.MarkedForClose) continue;
                    var beacon = b as IMyBeacon;
                    if (beacon == null || !beacon.IsWorking) continue;
                    var hud = beacon.HudText ?? string.Empty;
                    if (hud.IndexOf("arming", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    activeRadars.Add(b);
                }
            }

            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                long identityId = player.Identity.IdentityId;

                var controlled = player.Controller?.ControlledEntity?.Entity;
                var playerPos = controlled?.PositionComp?.WorldAABB.Center
                             ?? player.Character?.PositionComp?.WorldAABB.Center;
                if (playerPos == null) continue;

                foreach (var radar in activeRadars)
                {
                    var radarPos = radar.PositionComp.WorldAABB.Center;
                    double distance = Vector3D.Distance(playerPos.Value, radarPos);
                    var radarBeacon = radar as IMyBeacon;

                    if (distance <= RadarVisibilityRange)
                        continue;

                    var ownIdentities = new HashSet<long> { identityId };
                    var playerFaction = MySession.Static.Factions?.TryGetPlayerFaction(identityId);
                    if (playerFaction != null)
                        foreach (var memberId in playerFaction.Members.Keys)
                            ownIdentities.Add(memberId);

                    IMyFaction radarOwnerFaction = null;
                    Color gpsColor;
                    if (ownIdentities.Contains(radar.OwnerId))
                    {
                        gpsColor = new Color(0, 255, 0); // свой/фракционный
                    }
                    else
                    {
                        radarOwnerFaction = MySession.Static.Factions?.TryGetPlayerFaction(radar.OwnerId);

                        //Instance?.Log.Info($"radarGrid='{radar.CubeGrid?.DisplayName ?? "null"}', radarOwnerId={radar.OwnerId}, " +
                        //    $"radarOwnerFaction='{radarOwnerFaction?.Tag ?? "null"}'");

                        if (radarOwnerFaction == null)
                        {
                            gpsColor = new Color(255, 50, 50); // владелец радара без фракции = враг
                            //Instance?.Log.Info($"Radar color: player='{player.DisplayName}', radar='{radar.CubeGrid?.DisplayName}', result=RED (radar owner no faction)");
                        }
                        else if (playerFaction == null)
                        {
                            gpsColor = new Color(255, 50, 50); // игрок без фракции = все чужие враги
                            //Instance?.Log.Info($"Radar color: player='{player.DisplayName}', radar='{radar.CubeGrid?.DisplayName}', result=RED (player no faction)");
                        }
                        else
                        {
                            var relation = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(playerFaction.FactionId, radarOwnerFaction.FactionId);
                            switch (relation)
                            {
                                case MyRelationsBetweenFactions.Friends:
                                    gpsColor = new Color(0, 128, 0);
                                    break;
                                case MyRelationsBetweenFactions.Neutral:
                                    gpsColor = new Color(255, 255, 255);
                                    break;
                                default:
                                    if (radarOwnerFaction.Tag == "UNKN")
                                        gpsColor = new Color(255, 255, 255);
                                    else
                                        gpsColor = new Color(255, 50, 50);
                                    break;
                            }
                            //Instance?.Log.Info($"Radar relation result: " + $"radarFactionId={radarOwnerFaction?.FactionId}, " + $"relationValue={relation}");
                        }
                    }
                    var prefix = radarOwnerFaction == null ? "" : radarOwnerFaction?.Tag + '.';
                    var gps = CreateGpsColored(radarPos, prefix + radarBeacon.HudText, GpsDescriptionRadarSignal, gpsColor);
                    var gpsRef = gps;
                    gpsCollection.SendAddGpsRequest(identityId, ref gpsRef, 0L, false);
                }
            }
        }

        private MyGps CreateGpsColored(Vector3D position, string name, string description, Color color)
        {
            return new MyGps
            {
                Coords = position,
                Name = name,
                DisplayName = name,
                Description = description,
                GPSColor = color,
                IsContainerGPS = true,
                ShowOnHud = true,
                DiscardAt = new TimeSpan?()
            };
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="position"></param>
        /// <param name="name"></param>
        /// <param name="description"></param>
        /// <returns></returns>
        private MyGps CreateGps(Vector3D position, string name, string description)
        {
            MyGps gps = new MyGps
            {
                Coords = position,
                Name = name,
                DisplayName = name,
                Description = description,
                GPSColor = new Color(102, 255, 255),
                IsContainerGPS = true,
                ShowOnHud = true,
                DiscardAt = new TimeSpan?()
            };

            return gps;
        }



        // Запрещённые типы блоков (TypeId / MyObjectBuilderType)
        static readonly HashSet<MyObjectBuilderType> ForbiddenTypes = new HashSet<MyObjectBuilderType>
        {
            // Производство
            typeof(MyObjectBuilder_Assembler),
            typeof(MyObjectBuilder_Refinery),
            typeof(MyObjectBuilder_OxygenGenerator),

            // Хранилище
            typeof(MyObjectBuilder_CargoContainer),
            //typeof(MyObjectBuilder_Conveyor),
            //typeof(MyObjectBuilder_ConveyorConnector),
            
            // Энергия
            typeof(MyObjectBuilder_Reactor),
            typeof(MyObjectBuilder_BatteryBlock),
            typeof(MyObjectBuilder_HydrogenEngine),
            typeof(MyObjectBuilder_SolarPanel),
            typeof(MyObjectBuilder_WindTurbine),

            typeof(MyObjectBuilder_SafeZoneBlock),
            typeof(MyObjectBuilder_SurvivalKit),
            typeof(MyObjectBuilder_MedicalRoom),
            typeof(MyObjectBuilder_RadioAntenna),
            typeof(MyObjectBuilder_Beacon),
        };



        void AttachHandlers()
        {
            if (_handlersAttached)
                return;

            if (MySession.Static == null)
            {
                Log.Info("NoUndergroundBlocks: MySession.Static is null in AttachHandlers");
                return;
            }

            MyEntities.OnEntityAdd -= OnEntityAdd;
            MyEntities.OnEntityAdd += OnEntityAdd;

            _handlersAttached = true;
            Log.Info("NoUndergroundBlocks: Handlers attached (new grids only).");
        }

        void DetachHandlers()
        {
            if (!_handlersAttached)
                return;

            MyEntities.OnEntityAdd -= OnEntityAdd;
            _handlersAttached = false;

            // Очередь может содержать блоки старого мира — на новом сервере они мёртвые.
            while (_pendingBlockChecks.TryDequeue(out _)) { }

            Log.Info("NoUndergroundBlocks: Handlers detached.");
        }

        void OnEntityAdd(IMyEntity entity)
        {
            var grid = entity as MyCubeGrid;
            if (grid == null) return;
            if (grid.IsNpcSpawnedGrid) return;

            grid.OnBlockAdded -= OnBlockAdded;
            grid.OnBlockAdded += OnBlockAdded;

            var ig = grid as IMyCubeGrid;
            if (ig != null)
            {
                ig.OnIsStaticChanged -= OnGridIsStaticChanged;
                ig.OnIsStaticChanged += OnGridIsStaticChanged;
            }

            Log.Info($"NoUndergroundBlocks: New grid {grid.DisplayName} ({grid.EntityId}), IsStatic={grid.IsStatic}");

            // Не трогаем грид здесь! Только ставим в очередь — проверка будет
            // на следующем Update() в главном потоке, когда грид уже готов.
            if (grid.IsStatic)
            {
                foreach (var slim in grid.GetBlocks())
                    _pendingBlockChecks.Enqueue(slim);
            }
        }

        void OnBlockAdded(MySlimBlock slim)
        {
            if (slim == null) return;
            _pendingBlockChecks.Enqueue(slim);
        }

        void OnGridIsStaticChanged(IMyCubeGrid grid, bool toStatic)
        {
            if (!toStatic) return;
            if (!(grid is MyCubeGrid myGrid)) return;
            if (myGrid.IsNpcSpawnedGrid) return;

            foreach (var slim in myGrid.GetBlocks())
                _pendingBlockChecks.Enqueue(slim);
        }

        void TryCheckBlockOnStaticGrid(MySlimBlock slim)
        {
            if (slim == null || slim.CubeGrid == null) return;

            var grid = slim.CubeGrid as MyCubeGrid;
            if (grid == null) return;
            if (grid.Closed || grid.MarkedForClose) return;
            if (grid.Physics == null) return;
            if (!grid.InScene) return;
            if (!grid.IsStatic) return;
            if (slim.IsDestroyed) return;

            var def = slim.BlockDefinition;
            if (def == null) return;

            // Сначала дешёвая проверка типа — IsBelowTerrain дорогой,
            // не гоняем его для каждого блока.
            if (!ForbiddenTypes.Contains(def.Id.TypeId)) return;

            var worldPos = grid.GridIntegerToWorld(slim.Position);
            if (!IsBelowTerrain(worldPos)) return;

            long ownerId = slim.OwnerId;
            if (ownerId == 0 && grid.BigOwners.Count > 0)
                ownerId = grid.BigOwners[0];

            try
            {
                grid.RemoveBlock(slim, updatePhysics: true);
            }
            catch (Exception e)
            {
                Log.Error(e, "NoUndergroundBlocks: RemoveBlock failed");
                return;
            }

            var msg = $"Removed underground block of forbidden type '{def.Id.TypeId}'";
            Log.Info($"NoUndergroundBlocks: {msg}");

            if (ownerId != 0)
                MyVisualScriptLogicProvider.SendChatMessage(msg, "UndergroundCheck", ownerId, "Red");
        }

        bool IsBelowTerrain(Vector3D worldPos)
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.VoxelMaps == null)
                return false;

            var voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels);

            MyPlanet closestPlanet = null;
            double closestDistSq = double.MaxValue;

            foreach (var v in voxels)
            {
                var planet = v as MyPlanet;
                if (planet == null)
                    continue; // игнорируем астероиды

                var aabb = planet.PositionComp.WorldAABB;
                var contain = aabb.Contains(worldPos);
                if (contain == ContainmentType.Disjoint)
                    continue;

                var center = aabb.Center;
                var distSq = Vector3D.DistanceSquared(center, worldPos);
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closestPlanet = planet;
                }
            }

            if (closestPlanet == null)
                return false;

            var planetCenter = closestPlanet.PositionComp.WorldAABB.Center;
            var vec = worldPos - planetCenter;
            var distToCenter = vec.Length();

            var surfacePoint = closestPlanet.GetClosestSurfacePointGlobal(ref worldPos);
            var surfaceVec = surfacePoint - planetCenter;
            var distToSurface = surfaceVec.Length();

            double delta = distToSurface - distToCenter;

            // Блок ближе к центру, чем поверхность — считаем под грунтом (с небольшим допуском)
            return delta > 0.5;
        }

        /// <summary>
        /// Активна ли фракция за последние 7 дней. Проверяем по её членам:
        /// достаточно, чтобы хотя бы один имел свежую активность.
        /// </summary>
        private bool IsFactionActiveInLastWeek(IMyFaction faction)
        {
            if (faction == null)
                return false;

            if (_scores == null)
                return true; // защитный дефолт: если данных нет — не скрываем

            var cutoff = DateTime.UtcNow.AddDays(-7);
            var cutoffDate = cutoff.Date;

            foreach (var memberId in faction.Members.Keys)
            {
                if (!_scores.Players.TryGetValue(memberId, out var pInfo) || pInfo == null)
                    continue;

                if (pInfo.LastActivityUpdateUtc >= cutoff)
                    return true;

                var records = pInfo.RecentActivityRecords;
                if (records != null)
                {
                    for (int i = 0; i < records.Count; i++)
                    {
                        var rec = records[i];
                        if (rec != null && rec.Hours > 0 && rec.Timestamp >= cutoffDate)
                            return true;
                    }
                }
            }

            return false;
        }

        private bool IsFactionActiveInLastWeek(long factionId)
        {
            var faction = MySession.Static?.Factions?.TryGetFactionById(factionId);
            if (faction == null)
                return false;
            return IsFactionActiveInLastWeek(faction);
        }
    }
}

[JsonObject(MemberSerialization.OptIn)]
public class ScoreStorage
{
    // key: FactionId
    [JsonProperty]
    public Dictionary<long, FactionScoreInfo> Factions { get; set; }
        = new Dictionary<long, FactionScoreInfo>();

    // key: IdentityId
    [JsonProperty]
    public Dictionary<long, PlayerScoreInfo> Players { get; set; }
        = new Dictionary<long, PlayerScoreInfo>();

    [JsonProperty]
    public DateTime LastScoreTimeUtc { get; set; } = DateTime.MinValue;   // <-- добавлено
}
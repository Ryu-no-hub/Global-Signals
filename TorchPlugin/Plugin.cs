#define USE_HARMONY

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using HarmonyLib;
using Newtonsoft.Json;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
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

        public UserControl GetControl() => control ?? (control = new ConfigView());
        private ConfigView control;

        private TorchSessionManager sessionManager;

        private bool initialized;
        private bool failed;
        private int passed_5min = 0;
        public static bool debug = false;

        static readonly MyDefinitionId GlobalRadarDef = new MyDefinitionId(typeof(MyObjectBuilder_Beacon), "LG_D2A_R");
        //public static readonly HashSet<long> FactionsWithGlobalRadar = new HashSet<long>();
        public static readonly Dictionary<long, List<MyFunctionalBlock>> IdentityRadars = new Dictionary<long, List<MyFunctionalBlock>>();



        public string GpsDescriptionGlobalSignals { get { return Config.GpsDescriptionString; } }
        public bool UseConnectedGrids { get { return Config.UseConnectedGrids; } }
        //public List<KeyValuePair<MyCubeGrid, DateTime>> gridsDetectable = new List<KeyValuePair<MyCubeGrid, DateTime>>();
        public List<MyCubeGrid> gridsDetectable = new List<MyCubeGrid>();
        public DateTime lastFindTime = DateTime.Now;


        // Faction Scores
        public DateTime lastScoreTime = DateTime.Now;
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
            if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            {
                failed = true;
                return;
            }
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

        public static void UnregisterRadar(MyFunctionalBlock block)
        {
            long ownerId = block.OwnerId;
            if (ownerId <= 0)
                return;

            if (!IdentityRadars.TryGetValue(ownerId, out var list))
                return;

            list.Remove(block);
            if (list.Count == 0)
                IdentityRadars.Remove(ownerId);
        }

        public static void InitIdentityRadars()
        {
            IdentityRadars.Clear();

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

                    func.IsWorkingChanged -= RadarWorkingChanged;
                    func.IsWorkingChanged += RadarWorkingChanged;

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

                RadarWorkingChanged(func);
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
                    FactionScoreManager.CalculateCapturableVolume();
                    InitIdentityRadars();

                    break;

                case TorchSessionState.Unloading:
                    Log.Debug("Unloading");
                    if (initialized)
                    {
                        Log.Info("Unloading scores data into JSON");
                        SyncRecentActivityToScores();
                        SaveScores(StoragePath);
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


        public static void RadarWorkingChanged(IMyCubeBlock block)
        {
            var func = block as MyFunctionalBlock;
            if (func == null || func.BlockDefinition.Id.SubtypeName != "LG_D2A_R")
                return;

            long ownerId = func.OwnerId;

            foreach (var kv in IdentityRadars.ToList())
            {
                var list = kv.Value;
                if (list == null)
                    continue;

                if (list.Remove(func) && list.Count == 0)
                    IdentityRadars.Remove(kv.Key);
            }

            Instance?.Log.Info($"RadarWorkingChanged: Closed={func.Closed}, MarkedForClose={func.MarkedForClose}, " +
                $"OwnerId={ownerId}, IsWorking={func.IsWorking}");

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

        private void CustomUpdate()
        {
            // TODO: Put your update processing here. It is called on every simulation frame!
            if (initialized)
            {
                if (Config.GlobalSignals)
                {
                    if (DateTime.Now - lastFindTime > TimeSpan.FromSeconds(3))
                    {
                        FindGrids(Config.UseConnectedGrids);
                        ShowGrids();
                        lastFindTime = DateTime.Now;
                        //Log.Info("Update signals");
                    }
                }

                FactionScoreManager.TickActivity();
                FactionScoreManager.TickMovement();
                if (Config.FactionScore)
                {
                    // current UTC time
                    var utcNow = DateTime.UtcNow;

                    // "Local" time in UTC+3 (no DST assumed)
                    var mskNow = utcNow + TimeSpan.FromHours(3);

                    // Today 21:00 in UTC+3
                    var mskNine = mskNow.Date.AddHours(21);

                    // Corresponding UTC moment
                    var utcNine = mskNine - TimeSpan.FromHours(3);

                    bool shouldRunDaily = utcNow >= utcNine && lastScoreTime < utcNine;   // 21:00 UTC+3 once per day

                    bool shouldRunRestart = (utcNow - lastScoreTime > TimeSpan.FromHours(1)) || (passed_5min == 1); // 5 minutes after restart


                    if (/*AnyPlayerOnline() &&*/ (shouldRunDaily || shouldRunRestart))
                    {
                        passed_5min = 2;
                        lastScoreTime = DateTime.UtcNow;
                        StringBuilder resultSB = new StringBuilder();
                        resultSB = UpdateFactionsList(resultSB);
                        resultSB = ScoreFactions(resultSB);
                        PrintScores(resultSB);
                    }
                }
                if ((passed_5min == 0) && (DateTime.UtcNow - lastScoreTime > TimeSpan.FromMinutes(5)))
                    passed_5min = 1;
            }
            PatchHelpers.PatchUpdates();
        }

        public void PrintScores(StringBuilder resultSB)
        {
            MyVisualScriptLogicProvider.SendChatMessage(resultSB.ToString());
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

        public StringBuilder UpdateFactionsList(StringBuilder resultSB)
        {
            if (MySession.Static?.Factions?.Factions == null)
                return resultSB;

            //Log.Info($"{MySession.Static?.Factions?.Factions.Count}");
            foreach (var faction in MySession.Static.Factions.Factions)
            {
                if (faction.Value.IsEveryoneNpc())
                    continue;

                if (faction.Value.Tag == "SPID")
                    continue;

                //Log.Info($"UpdateFactionsList: Faction found: {faction.Value.Name}");
                //resultSB.AppendLine($"UpdateFactionsList: Faction found: {faction.Value.Name}.");
                //MyVisualScriptLogicProvider.SendChatMessage($"Faction found: {faction.Value.Name}");

                if (!_scores.Factions.TryGetValue(faction.Key, out var f))
                {
                    //Log.Info($"UpdateFactionsList: Adding FactionScoreInfo for new faction '{f.Name}'");
                    //resultSB.AppendLine($"UpdateFactionsList: Adding FactionScoreInfo for new faction '{f.Name}'");
                    //MyVisualScriptLogicProvider.SendChatMessage("Adding");
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

                }

                // TODO: Check if faction deleted to delete it from factionScores aswell?
            }

            return resultSB;
        }

        public StringBuilder ScoreFactions(StringBuilder resultSB)
        {
            if (MySession.Static?.Factions?.Factions == null)
                return resultSB;

            int count = 0;
            oldPlayerScores.Clear();
            FactionScoreManager.factionPlanetCoveredFraction.Clear();

            var factionScoresKeys = _scores.Factions.Keys.ToList();  // Snapshot copy
            foreach (var factionId in factionScoresKeys)
            {
                IMyFaction faction = MySession.Static.Factions.TryGetFactionById(factionId);
                if (faction == null) continue;

                Log.Info($"ScoreFactions: Scoring faction {faction.Name}");
                int factionScore = 0, factionPower = 0, factionActivity = 0;
                int factionScoreDelta, factionPowerDelta, factionActivityDelta;
                float factionTerritoryDelta;
                double factionHours2WDelta;

                float factionTerritory = 1;
                double factionHours2W = 0;
                string factionResultText;
                count++;

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

                    //resultSB.AppendLine(resultText);

                    UpdatePlayerScore(playerId, playerName, playerSteamId, playerScore);
                }

                // add faction scores print
                if (_scores.Factions.TryGetValue(factionId, out var fInfo))
                {
                    factionScoreDelta = factionScore - fInfo.Score;
                    factionPowerDelta = factionPower - fInfo.Power;
                    factionTerritoryDelta = factionTerritory - fInfo.TerritoryControl;
                    factionActivityDelta = factionActivity - fInfo.Activity;
                    factionHours2WDelta = factionHours2W - fInfo.Hours2W;
                    factionResultText = $"Faction score for [{fInfo.Tag}] = {factionScore} ({(factionScoreDelta >= 0 ? "+" : "")}{factionScoreDelta}): " +
                        $"\n - Power: {factionPower} ({(factionPowerDelta >= 0 ? "+" : "")}{factionPowerDelta})" +
                        $"\n - Territory Control: {factionTerritory} ({(factionTerritoryDelta >= 0 ? "+" : "")}{factionTerritoryDelta})" +
                        $"\n - Activity: {factionActivity} [{factionHours2W:0.00}h]  ({(factionActivityDelta >= 0 ? "+" : "")}{factionActivityDelta} [{(factionHours2WDelta >= 0 ? "+" : "")}{factionHours2WDelta:0.00}h])";

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

                    factionResultText = $"Faction score for NEW FACTION [{fInfo.Tag}]: = {factionScore}: \n - Power: {factionPower} \n - Territory Control: {factionTerritory} \n - Activity: {factionActivity} [{factionHours2W:0.00}h]";
                }

                Log.Info(factionResultText);
                resultSB.AppendLine(factionResultText);

                resultSB = FactionScoreManager.CapturedResults(factionId, resultSB);
                //

                fInfo.ScoreHistory.Add(new FactionScoreHistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    Score = factionScore
                });

                //// Чтобы история не разрасталась бесконечно:
                //const int MaxHistoryEntries = 1000;
                //if (fInfo.ScoreHistory.Count > MaxHistoryEntries)
                //    fInfo.ScoreHistory.RemoveRange(0, fInfo.ScoreHistory.Count - MaxHistoryEntries);

            }

            if (count > 0)
                resultSB.Append("\nFactions: ").Append(count);

            //MyVisualScriptLogicProvider.SendChatMessage("ScoreFactions: Complete!");
            //resultSB.AppendLine("ScoreFactions: Complete!\n");
            //Log.Info("ScoreFactions: Complete!");

            //resultSB = ShowPlayerScores(resultSB);
            //resultSB = ShowFactionScores(resultSB);

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

        void SaveScores(string instancePath)
        {
            var path = Path.Combine(instancePath, "Scores.json");
            var json = JsonConvert.SerializeObject(_scores, Formatting.Indented);
            File.WriteAllText(path, json);
        }


        public StringBuilder ShowFactionScores(StringBuilder resultSB)
        {
            if (MySession.Static?.Factions?.Factions == null)
                return resultSB;

            var count = 0;

            foreach (var kvp in _scores.Factions)
            {
                var fInfo = kvp.Value;
                resultSB.AppendLine($"Faction score for [{fInfo.Tag}] = {fInfo.Score}");
                count++;
            }
            if (count > 0)
                resultSB.Append("Factions: ").Append(count);

            if (debug) Log.Info(resultSB.ToString());

            return resultSB;
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

                        if (cubeGrid.IsNpcSpawnedGrid) continue;
                        if (cubeGrid.Physics == null) continue;
                        if (cubeGrid.NaturalGravity.Length() > 0) continue;

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

                        if (cubeGrid.IsNpcSpawnedGrid) continue;
                        if (cubeGrid.Physics == null) continue;
                        if (cubeGrid.NaturalGravity.Length() > 0) continue;

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

                if (allowingRadars.Count == 0)
                    continue;

                // Для каждого радара — identity владельца + его фракция
                // Грид скрыт только если ВСЕ разрешающие радары принадлежат той же фракции, что и грид
                foreach (var grid in gridsDetectable)
                {
                    // Проверяем: есть ли хоть один радар, чья фракция НЕ совпадает с фракцией грида
                    bool shouldShow = false;
                    foreach (var radarBeacon in allowingRadars)
                    {
                        long radarOwnerId = radarBeacon.OwnerId;
                        var radarFaction = MySession.Static.Factions?.TryGetPlayerFaction(radarOwnerId);

                        bool gridBelongsToRadarOwner = grid.BigOwners.Any(gridOwner =>
                        {
                            if (gridOwner == radarOwnerId) return true;
                            if (radarFaction == null) return false;
                            return radarFaction.Members.ContainsKey(gridOwner);
                        });

                        if (!gridBelongsToRadarOwner)
                        {
                            shouldShow = true;
                            break;
                        }
                    }

                    if (!shouldShow)
                        continue;

                    Vector3D position = grid.PositionComp.WorldAABB.Center;
                    MyGps gps = CreateGps(position, grid.DisplayName, GpsDescriptionGlobalSignals);
                    MyGps gpsRef = gps;

                    long entity = 0L;
                    gpsCollection.SendAddGpsRequest(identityId, ref gpsRef, entity, false);
                }
            }
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

                    if (desc == null)
                        continue;

                    if (!desc.Contains(GpsDescriptionGlobalSignals))
                        continue;

                    MyAPIGateway.Session?.GPS.RemoveGps(identity.IdentityId, gps);
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
                        var tf = MyAPIGateway.Session.Factions.TryGetPlayerFaction(targetPlayerId);
                        if (tf == null) return false;
                        var prop = logicType.GetProperty("SharedFactionIds");
                        if (prop == null) return false;
                        var set = prop.GetValue(gameLogic) as IEnumerable<long>;
                        return set != null && set.Contains(tf.FactionId);
                    }

                case 4: // Players
                    {
                        var prop = logicType.GetProperty("SharedPlayerIds");
                        if (prop == null) return false;
                        var set = prop.GetValue(gameLogic) as IEnumerable<long>;
                        return set != null && set.Contains(targetPlayerId);
                    }

                default:
                    return false;
            }
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
}
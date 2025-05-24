#define USE_HARMONY

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
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
using Torch.Commands.Permissions;
using Torch.Commands;
using Torch.Session;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using VRage.RemoteClient.Core;
using Sandbox.Game.GameSystems;
using System.Windows.Documents;
using Torch.Server.ViewModels.Entities;
using VRage.ModAPI;
using static Sandbox.Game.AI.Pathfinding.Obsolete.MyGridPathfinding;
using Sandbox.Engine.Multiplayer;
using VRage.Game.Components;
using static VRage.Profiler.MyProfilerBlock;

namespace TorchPlugin
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class Plugin : TorchPluginBase, IWpfPlugin, ICommonPlugin
    {
        public const string PluginName = "GlobalSignals";
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
        public string GpsIdentifierName { get { return Config.GpsDescriptionString; } }
        public bool UseConnectedGrids { get { return Config.UseConnectedGrids; } }
        //public List<KeyValuePair<MyCubeGrid, DateTime>> gridsDetectable = new List<KeyValuePair<MyCubeGrid, DateTime>>();
        public List<MyCubeGrid> gridsDetectable = new List<MyCubeGrid>();
        public DateTime lastFindTime = DateTime.Now;

        // ReSharper disable once UnusedMember.Local
        private readonly Commands commands = new Commands();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public override void Init(ITorchBase torch)
        {
            base.Init(torch);

#if DEBUG
            // Allow the debugger some time to connect once the plugin assembly is loaded
            Thread.Sleep(100);
#endif

            Instance = this;

            Log.Info("Init");

            var configPath = Path.Combine(StoragePath, ConfigFileName);
            config = PersistentConfig<PluginConfig>.Load(Log, configPath);

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

        private void SessionStateChanged(ITorchSession session, TorchSessionState newstate)
        {
            switch (newstate)
            {
                case TorchSessionState.Loading:
                    Log.Debug("Loading");
                    break;

                case TorchSessionState.Loaded:
                    Log.Debug("Loaded");
                    break;

                case TorchSessionState.Unloading:
                    Log.Debug("Unloading");
                    break;

                case TorchSessionState.Unloaded:
                    Log.Debug("Unloaded");
                    break;
            }
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

        public void RemoveGpsFromAllPlayers()
        {

            //Log.Info("Removing GPS from all Players.");

            foreach (var identity in MySession.Static.Players.GetAllIdentities())
                RemoveGpsFromPlayer(identity.IdentityId);
        }

        public void RemoveGpsFromPlayer(long idendity)
        {

            List<IMyGps> gpsList = MyAPIGateway.Session?.GPS.GetGpsList(idendity);

            if (gpsList == null)
                return;

            foreach (IMyGps gps in gpsList)
            {

                if (!(gps is MyGps myGps))
                    continue;

                string desc = myGps.Description;

                if (desc == null)
                    continue;

                if (!desc.Contains(GpsIdentifierName))
                    continue;

                MyAPIGateway.Session?.GPS.RemoveGps(idendity, gps);
            }
        }
        private void CustomUpdate()
        {
            // TODO: Put your update processing here. It is called on every simulation frame!
            if (initialized)
            {
                if (DateTime.Now - lastFindTime > TimeSpan.FromSeconds(3))
                {
                    FindGrids(true);
                    ShowGrids();
                    lastFindTime = DateTime.Now;
                    //Log.Info("Update signals");
                }
            }
            //{
                //foreach (var grid in gridsDetectable)
                //{
                //    grid.NaturalGravity
                //}

                //// Process Radar Blocks
                //RadarProcess.Process();

                //// Process Hud Markers
                //HudMarkManager.Process();

                //m_lastUpdate = DateTime.Now;
            //}
            PatchHelpers.PatchUpdates();
        }

        public void FindGrids(bool connected)
        {
            List<MyCubeGrid> gridsList = new List<MyCubeGrid>();

            if (connected)
            {

                foreach (var group in MyCubeGridGroups.Static.Physical.Groups)
                {
                    foreach (var groupNode in group.Nodes)
                    {
                        MyCubeGrid cubeGrid = groupNode.NodeData;

                        if (cubeGrid.Physics == null)
                            continue;

                        if (cubeGrid.NaturalGravity.Length() > 0)
                            continue;

                        gridsList.Add(cubeGrid);
                    }
                }
            }
            else
            {

                foreach (var group in MyCubeGridGroups.Static.Mechanical.Groups)
                {
                    foreach (var groupNodes in group.Nodes)
                    {
                        MyCubeGrid cubeGrid = groupNodes.NodeData;

                        if (cubeGrid.Physics == null)
                            continue;

                        if (cubeGrid.NaturalGravity.Length() > 0)
                            continue;

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

            List<MyCubeGrid> gridsList = gridsDetectable;

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                foreach (var grid in gridsList)
                {
                    Vector3D position = grid.PositionComp.GetPosition();

                    //var description = ($"Detected by Global Radar");

                    MyGps gps = CreateGps(position, grid.DisplayName, GpsIdentifierName);
                    MyGps gpsRef = gps;

                    long entity = 0L;
                    gpsCollection.SendAddGpsRequest(player.Identity.IdentityId, ref gpsRef, entity, false);
                }
            }

        }
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

        //public void Clean()
        //{
        //    var gridsToRemove = new List<MyCubeGrid>();

        //    foreach (var group in MyCubeGridGroups.Static.Physical.Groups)
        //    {
        //        foreach (var groupNode in group.Nodes)
        //        {
        //            MyCubeGrid cubeGrid = groupNode.NodeData;

        //            if (cubeGrid.Physics == null)
        //                continue;

        //            bool hasOwnerPlate = false;
        //            var blocks = cubeGrid.GetBlocks();
        //            MyVisualScriptLogicProvider.SendChatMessage($"Checking grid {cubeGrid.DisplayName}");

        //            foreach (var block in blocks)
        //            {
        //                if ((block.BlockDefinition.Id.SubtypeName == "OwnerSEPSE.Small") || (block.BlockDefinition.Id.SubtypeName == "OwnerSEPSE.Large"))
        //                {
        //                    hasOwnerPlate = true;
        //                    MyVisualScriptLogicProvider.SendChatMessage($"Grid {cubeGrid.DisplayName} has ownerblock {block.BlockDefinition.DisplayNameText}");
        //                    break; // no need to continue once found
        //                }
        //            }

        //            if (!hasOwnerPlate)
        //            {
        //                // Mark for removal instead of removing immediately
        //                gridsToRemove.Add(cubeGrid);
        //            }
        //        }
        //    }
        //    foreach (var grid in gridsToRemove)
        //    {
        //        MyVisualScriptLogicProvider.SendChatMessage($"Deleted: {grid.DisplayName}");
        //        MyAPIGateway.Entities.RemoveEntity(grid);
        //    }
        //}
    }
}
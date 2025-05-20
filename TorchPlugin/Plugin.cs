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
using VRage.Game.ModAPI;
using VRage.Utils;

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

        public GlobalSignalsConfig Config => config?.Data;
        private PersistentConfig<GlobalSignalsConfig> config;
        private static readonly string ConfigFileName = $"{PluginName}.cfg";

        public UserControl GetControl() => control ?? (control = new ConfigView());
        private ConfigView control;

        private TorchSessionManager sessionManager;

        private bool initialized;
        private bool failed;
        public string GpsIdentifierName { get { return Config.GpsDescriptionString; } }
        public bool UseConnectedGrids { get { return Config.UseConnectedGrids; } }
        public List<MyCubeGrid> gridsDetectable = new List<MyCubeGrid>();

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
            config = PersistentConfig<GlobalSignalsConfig>.Load(Log, configPath);

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

            Log.Info("Removing GPS from all Players.");

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

                if (!desc.Contains("by " + GpsIdentifierName) || !desc.Contains("Top Grid:"))
                    continue;

                MyAPIGateway.Session?.GPS.RemoveGps(idendity, gps);
            }
        }
        private void CustomUpdate()
        {
            // TODO: Put your update processing here. It is called on every simulation frame!

            PatchHelpers.PatchUpdates();
        }
    }
}
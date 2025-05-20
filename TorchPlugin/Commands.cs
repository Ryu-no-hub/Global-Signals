using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using System.Collections.Generic;
using System;
using Shared.Config;
using Shared.Plugin;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;
using NLog;
using VRage.Collections;
using VRage.Groups;
using VRage.Game;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GUI;
using static Sandbox.Game.Gui.MyGuiScreenToolbarConfigBase;

namespace TorchPlugin
{
    public class Commands : CommandModule
    {
        //private static IPluginConfig Config => Common.Config;
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public Plugin Plugin => (Plugin)Context.Plugin;

        private void Respond(string message)
        {
            Context?.Respond(message);
        }

        // TODO: Replace cmd with the name of your chat command
        // TODO: Implement subcommands as needed
        private void RespondWithHelp()
        {
            Respond("GlobalSignals commands:");
            Respond("  !findgrigs info");
            Respond("    Prints the current configuration settings.");
            Respond("  !findgrigs enable");
            Respond("    Enables the plugin");
            Respond("  !findgrigs disable");
            Respond("    Disables the plugin");
            Respond("  !findgrigs subcmd <name> <value>");
            Respond("    TODO Your subcommand");
        }

        private void RespondWithInfo()
        {
            var config = Plugin.Instance.Config;
            Respond($"{Plugin.PluginName} plugin is enabled: {Format(config.Enabled)}");
            // TODO: Respond with your plugin settings
            // For example:
            //Respond($"custom_setting: {Format(config.CustomSetting)}");
        }

        // Custom formatters

        private static string Format(bool value) => value ? "Yes" : "No";

        // Custom parsers

        private static bool TryParseBool(string text, out bool result)
        {
            switch (text.ToLower())
            {
                case "1":
                case "on":
                case "yes":
                case "y":
                case "true":
                case "t":
                    result = true;
                    return true;

                case "0":
                case "off":
                case "no":
                case "n":
                case "false":
                case "f":
                    result = false;
                    return true;
            }

            result = false;
            return false;
        }


        [Command("findgrigs help", "GlobalSignals: Help")]
        [Permission(MyPromoteLevel.None)]
        public void Help()
        {
            RespondWithHelp();
        }

        [Command("findgrigs info", "GlobalSignals: Prints the current settings")]
        [Permission(MyPromoteLevel.None)]
        public void Info()
        {
            RespondWithInfo();
        }

        [Command("findgrigs enable", "GlobalSignals: Enables the plugin")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enable()
        {
            Plugin.Config.Enabled = true;
            RespondWithInfo();
        }

        [Command("findgrigs disable", "GlobalSignals: Disables the plugin")]
        [Permission(MyPromoteLevel.Admin)]
        public void Disable()
        {
            Plugin.Config.Enabled = false;
            RespondWithInfo();
        }

        [Command("findgrids", "Finds detectable grids")]
        [Permission(MyPromoteLevel.Admin)]
        public void FindGrids(bool connected)
        {
            //List<KeyValuePair<long, List<MyCubeGrid>>> gridsList = new List<KeyValuePair<long, List<MyCubeGrid>>>();
            List<MyCubeGrid> gridsList = new List<MyCubeGrid>();

            if (connected)
            {

                foreach (var group in MyCubeGridGroups.Static.Physical.Groups)
                {
                    //var grids = CheckGroupsPcu(group.Nodes, Plugin.Config);
                    foreach (var groupNodes in group.Nodes)
                    {
                        MyCubeGrid cubeGrid = groupNodes.NodeData;

                        if (cubeGrid.Physics == null)
                            continue;

                        if (cubeGrid.NaturalGravity.Length() > 0)
                            continue;

                        gridsList.Add(cubeGrid);

                        //pcu += cubeGrid.BlocksPCU;

                        //if (config.ExcludeProjectionPCU)
                        //    pcu -= CountProjectionPCU(cubeGrid);
                    }
                }
            }
            else
            {

                foreach (var group in MyCubeGridGroups.Static.Mechanical.Groups)
                {

                    //var grids = CheckGroupsPcu(group.Nodes, Plugin.Config);

                    //if (grids.Value.Count > 0)
                    //    gridsList.Add(grids);

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
            Plugin.gridsDetectable = gridsList;

            //gridsList.Sort(delegate (KeyValuePair<long, List<MyCubeGrid>> pair1, KeyValuePair<long, List<MyCubeGrid>> pair2) {
            //    return pair2.Key.CompareTo(pair1.Key);
            //});

            //Context.Respond("SendGridsInternal");
            //return gridsList;
        }

        [Command("showgrids", "Shows detectable grids")]
        [Permission(MyPromoteLevel.Admin)]
        public void ShowGrids(string name, string value)
        {
            Plugin.RemoveGpsFromAllPlayers();

            MyGpsCollection gpsCollection = (MyGpsCollection)MyAPIGateway.Session?.GPS;

            if (gpsCollection == null)
                return;

            List<MyCubeGrid> gridsList = Plugin.gridsDetectable;

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                foreach (var grid in gridsList)
                {
                    Vector3D position = grid.PositionComp.GetPosition();

                    var description = ($"Detected by Global Radar");

                    //IMyGps gps = MyAPIGateway.Session.GPS.Create(grid.DisplayName, description, position, true);
                    //gpsCollection.AddPlayerGps AddGps(entity, gps);
                    MyGps gps = CreateGps(position, grid.DisplayName, description);
                    MyGps gpsRef = gps;

                    //MyGps gps = new MyGps()

                    long entity = 0L;
                    gpsCollection.SendAddGpsRequest(player.Identity.IdentityId, ref gpsRef, entity);
                }
            }

            //HashSet<Vector3> gpsSet = new HashSet<Vector3>();
            //List<MyGps> gpsList = new List<MyGps>();

            //long seconds = GetTimeMs();
            //AddToList(gpsSet, gpsList, FindGrids(Plugin.UseConnectedGrids));

            //if (gpsSet.Count > 0)
            //    SendGps(gpsList, Plugin.Config);

            Context.Respond("Showing grids");
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
            //gps.UpdateHash();
            //gps.SetEntityId(grid.EntityId);

            return gps;
        }

        //private KeyValuePair<long, List<MyCubeGrid>> CheckGroupsPcu(HashSetReader<MyGroups<MyCubeGrid, MyGridMechanicalGroupData>.Node> nodes, GlobalSignalsConfig config)
        //{

        //    List<MyCubeGrid> gridsList = new List<MyCubeGrid>();
        //    long pcu = 0;

        //    foreach (var groupNodes in nodes)
        //    {

        //        MyCubeGrid cubeGrid = groupNodes.NodeData;

        //        if (cubeGrid.Physics == null)
        //            continue;

        //        if (!IsGridInGravity(cubeGrid, config))
        //            continue;

        //        gridsList.Add(cubeGrid);

        //        pcu += cubeGrid.BlocksPCU;

        //        if (config.ExcludeProjectionPCU)
        //            pcu -= CountProjectionPCU(cubeGrid);
        //    }

        //    return new KeyValuePair<long, List<MyCubeGrid>>(pcu, gridsList);
        //}

        //private KeyValuePair<long, List<MyCubeGrid>> CheckGroupsPcu(HashSetReader<MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Node> nodes, GlobalSignalsConfig config)
        //{

        //    List<MyCubeGrid> gridsList = new List<MyCubeGrid>();
        //    long pcu = 0;

        //    foreach (var groupNodes in nodes)
        //    {

        //        MyCubeGrid cubeGrid = groupNodes.NodeData;

        //        if (cubeGrid.Physics == null)
        //            continue;

        //        if (!IsGridInsideFilter(cubeGrid, config))
        //            continue;

        //        gridsList.Add(cubeGrid);

        //        pcu += cubeGrid.BlocksPCU;

        //        if (config.ExcludeProjectionPCU)
        //            pcu -= CountProjectionPCU(cubeGrid);
        //    }

        //    return new KeyValuePair<long, List<MyCubeGrid>>(pcu, gridsList);
        //}

        //private void AddToList(HashSet<Vector3> positions, List<MyGps> returnList, List<MyGps> inputList)
        //{

        //    foreach (var gps in inputList)
        //    {

        //        if (positions.Contains(gps.Coords))
        //            continue;

        //        positions.Add(gps.Coords);
        //        returnList.Add(gps);
        //    }
        //}

        //private List<MyGps> FindGrids(IGridDetectionStrategy gridDetectionStrategy, int min, int distance, bool ignoreOffline, bool ignoreNpcs, long seconds)
        //{

        //    List<KeyValuePair<long, List<MyCubeGrid>>> grids = gridDetectionStrategy.FindGrids(Plugin.Config, Plugin.UseConnectedGrids);
        //    List<KeyValuePair<long, List<MyCubeGrid>>> filteredGrids = gridDetectionStrategy.GetFilteredGrids(grids,
        //        min, distance, Plugin.TopGrids, ignoreOffline, ignoreNpcs);

        //    List<MyGps> gpsList = new List<MyGps>();

        //    int i = 0;

        //    foreach (KeyValuePair<long, List<MyCubeGrid>> pair in filteredGrids)
        //    {

        //        i++;

        //        MyCubeGrid grid = pair.Value[0]; /* Cannot be empty because where do the PCUs come from? */

        //        var position = grid.PositionComp.GetPosition();

        //        if (Plugin.LogBroadcastedGrids)
        //            LogGrid(grid, gridDetectionStrategy);

        //        MyGps gps = CreateGps(i, grid, seconds, gridDetectionStrategy.GetDetectionType());

        //        gpsList.Add(gps);
        //    }

        //    return gpsList;
        //}

        //private void LogGrid(MyCubeGrid grid, IGridDetectionStrategy gridDetectionStrategy)
        //{

        //    try
        //    {

        //        long ownerId = OwnershipUtils.GetOwner(grid);

        //        string name = PlayerUtils.GetPlayerNameById(ownerId);

        //        IMyFaction faction = GetFactionForPlayer(ownerId);

        //        string factionString = "";
        //        if (faction != null)
        //            factionString = "[" + faction.Tag + "]";

        //        string ownedString = "Owned by: " + name + " " + factionString;

        //        long gridId = grid.EntityId;
        //        string gridName = grid.DisplayName;

        //        Log.Info("Broadcasted " + gridId + " " + gridName + " " + ownedString + " for '" + gridDetectionStrategy.GetStrategyName() + "'");

        //    }
        //    catch (Exception e)
        //    {
        //        Log.Error(e);
        //    }
        //}

        //private void SendGps(IEnumerable<MyGps> gpsSet, IPluginConfig config)
        //{

        //    MyGpsCollection gpsCollection = (MyGpsCollection)MyAPIGateway.Session?.GPS;

        //    if (gpsCollection == null)
        //        return;

        //    bool followGrids = config.GpsFollowGrids;
        //    bool playSound = config.PlayGpsSound;

        //    foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
        //    {
        //        foreach (MyGps gps in gpsSet)
        //        {

        //            MyGps gpsRef = gps;

        //            long entityId = 0L;
        //            if (followGrids)
        //                entityId = gps.EntityId;

        //            gpsCollection.SendAddGpsRequest(player.Identity.IdentityId, ref gpsRef, entityId, playSound);
        //        }
        //    }
        //}
        //private long GetTimeMs()
        //{
        //    return (long)(DateTime.Now.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
        //}
    }
}
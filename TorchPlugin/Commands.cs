using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using NLog;
using System.Text;

namespace TorchPlugin
{
    public class Commands : CommandModule
    {
        //private static IPluginConfig Config => Common.Config;
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        StringBuilder resultSB = new StringBuilder();

        public Plugin Plugin => (Plugin)Context.Plugin;

        private void Respond(string message)
        {
            Context?.Respond(message);
        }

        // TODO: Replace cmd with the name of your chat command
        // TODO: Implement subcommands as needed
        private void RespondWithHelp()
        {
            Respond("Global Mechanics commands:");
            Respond("  !gm settings");
            Respond("    Show current configuration");

            Respond("  !gm scores update");
            Respond("    Updates factions scores");

            Respond("  !gm scores showfactions");
            Respond("    Displays factions scores");

            Respond("  !gm scores enlistfactions");
            Respond("    Update factions list");

            Respond("  !gm scores drawpoints");
            Respond("    Draw covered planet points (Earthlike)");

            Respond("  !gm scores removepoints");
            Respond("    Remove covered planet points (Earthlike)");

            Respond("  !gm gs enable");
            Respond("    Enables global signals in space");

            Respond("  !gm gs disable");
            Respond("    Disables global signals in space");

            Respond("  !gm gs findgrids");
            Respond("    Finds detectable grids");

            Respond("  !gm gs showgrids");
            Respond("    Shows detectable grids");

            //Respond("  !globalsignals subcmd <name> <value>");
            //Respond("    TODO Your subcommand");
        }

        private void RespondWithInfo()
        {
            StringBuilder sb = new StringBuilder();
            var config = Plugin.Instance.Config;

            sb.AppendLine($"{Plugin.PluginName}:");
            sb.AppendLine($"Global signals: enabled: {Format(config.GlobalSignals)}");
            sb.AppendLine($"Global signals: use connected grids: {Format(config.UseConnectedGrids)}");
            sb.AppendLine($"Global signals: gps description string: '{config.GpsDescriptionString}'");
            sb.AppendLine($"Faction scoring enabled: {Format(config.FactionScore)}");

            Respond(sb.ToString());
            // TODO: Respond with your plugin settings
            // For example:
            //Respond($"custom_setting: {Format(config.CustomSetting)}");
        }

        private void RespondWithFactionScores(string scores_result)
        {
            Respond($"{Plugin.PluginName}: \nScores: {scores_result}");
        }

        //private void RespondWithFactionsList(string factions_list)
        //{
        //    Respond($"{Plugin.PluginName}: Factions List: {factions_list}");
        //}

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


        [Command("gm help", "Global Mechanics: Help")]
        [Permission(MyPromoteLevel.None)]
        public void Help()
        {
            RespondWithHelp();
        }

        [Command("gm settings", "Show current configuration")]
        [Permission(MyPromoteLevel.None)]
        public void Info()
        {
            RespondWithInfo();
        }

        [Command("gm scores update", "Updates factions scores")]
        [Permission(MyPromoteLevel.Admin)]
        public void UpdScores()
        {
            resultSB.Clear();
            resultSB = Plugin.UpdateFactionsList(resultSB);
            resultSB = Plugin.ScoreFactions(resultSB);
            Plugin.PrintScores(resultSB);
            //RespondWithFactionScores(resultSB.ToString());
        }

        [Command("gm scores showfactions", "Displays factions scores")]
        [Permission(MyPromoteLevel.Admin)]
        public void ShowScores()
        {
            resultSB.Clear();
            resultSB = Plugin.ShowFactionScores(resultSB);
            RespondWithFactionScores(resultSB.ToString());

            //var ply = Context?.Player;
            //if (ply != null)
            //    Plugin.ShowFactionScores(ply.IdentityId);
            //else
            //    Plugin.ShowFactionScores();
        }

        [Command("gm scores enlistfactions", "Update factions list")]
        [Permission(MyPromoteLevel.Admin)]
        public void UpdFactionsList()
        {
            resultSB.Clear();
            resultSB = Plugin.UpdateFactionsList(resultSB);
            Respond($"{Plugin.PluginName}: Factions List: {resultSB}");
        }

        [Command("gm scores drawpoints", "Draw covered planet points (Earthlike)")]
        [Permission(MyPromoteLevel.Admin)]
        public void DrawPoints()
        {
            long playerId = 0;
            var ply = Context?.Player;
            if (ply != null)
                playerId = ply.IdentityId;

            Plugin.SendTerritoryDebugGpsToAsker(true, ply.IdentityId);
            Respond($"{Plugin.PluginName}: DrawPoints gps are sent to you. Disappear in 3 minutes.");
        }

        [Command("gm scores removepoints", "Remove covered planet points (Earthlike)")]
        [Permission(MyPromoteLevel.Admin)]
        public void RemovePoints()
        {
            long playerId = 0;
            var ply = Context?.Player;
            if (ply != null)
                playerId = ply.IdentityId;

            Plugin.SendTerritoryDebugGpsToAsker(false, ply.IdentityId);
            Respond($"{Plugin.PluginName}: DrawPoints gps are removed.");
        }

        [Command("gm gs enable", "Enables global signals in space")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enable()
        {
            Plugin.Config.GlobalSignals = true;
            RespondWithInfo();
        }

        [Command("gm gs disable", "Disables global signals in space")]
        [Permission(MyPromoteLevel.Admin)]
        public void Disable()
        {
            Plugin.Config.GlobalSignals = false;
            Plugin.RemoveGpsFromAllPlayers();
            RespondWithInfo();
        }

        [Command("gm gs findgrids", "Finds detectable grids")]
        [Permission(MyPromoteLevel.Admin)]
        public void FindGridsCommand(bool connected)
        {            
            Plugin.FindGrids(true);
            Respond("Grids found.");
        }

        [Command("gm gs showgrids", "Shows detectable grids")]
        [Permission(MyPromoteLevel.Admin)]
        public void ShowGridsCommand()
        {
            Plugin.ShowGrids();
            Respond("Grids shown.");
        }

        [Command("gm gs hidegrids", "Hides detectable grids")]
        [Permission(MyPromoteLevel.Admin)]
        public void HideGridsCommand()
        {
            Plugin.RemoveGpsFromAllPlayers();
            Respond("Grids hidden.");
        }
    }
}
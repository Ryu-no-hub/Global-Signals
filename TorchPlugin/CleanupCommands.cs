using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace TorchPlugin
{
    /// <summary>
    /// Команды очистки гридов. Torch автоматически подхватывает все CommandModule
    /// из сборки плагина, поэтому регистрировать этот класс в Plugin.cs не нужно.
    ///
    /// Subtypes передаются ОДНИМ аргументом через запятую, без пробелов:
    ///   !gm cleanup list SubtypeA,SubtypeB
    /// </summary>
    public class CleanupCommands : CommandModule
    {
        /// <summary>Верхняя граница задержки удаления (30 суток) — защита от опечаток вроде 24000.</summary>
        private const double MaxDelayHours = 24 * 30;

        private void Respond(string message)
        {
            Context?.Respond(message);
        }

        /// <summary>
        /// Определяет правило: из аргумента (и сохраняет его) либо ранее сохранённое.
        /// Неизвестные subtypeId отклоняются — опечатка иначе привела бы к удалению всего.
        /// Пока запланировано удаление, правило менять нельзя: иначе игроки видели бы одно,
        /// а удалялось бы другое.
        /// </summary>
        private bool TryResolveRule(string raw, out List<string> rule)
        {
            rule = null;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = GridCleanupManager.ParseSubtypes(raw);
                if (parsed.Count == 0)
                {
                    Respond("No subtypeIds parsed. Example: !gm cleanup list SubtypeA,SubtypeB");
                    return false;
                }

                var unknown = GridCleanupManager.FindUnknownSubtypes(parsed);
                if (unknown.Count > 0)
                {
                    Respond($"Unknown block subtypeId(s): {string.Join(", ", unknown)}. Nothing was changed.");
                    return false;
                }

                var pending = GridCleanupManager.Pending;
                if (pending != null && !GridCleanupManager.SameRule(pending.Rule, parsed))
                {
                    Respond($"A deletion is scheduled with rule: {string.Join(", ", pending.Rule)} " +
                            $"({GridCleanupManager.DescribeDue(pending.DueUtc)}). " +
                            "Cancel it first with '!gm cleanup cancel' to change the rule. Nothing was changed.");
                    return false;
                }

                GridCleanupManager.SetRule(parsed);
                rule = parsed;
                return true;
            }

            var saved = GridCleanupManager.Rule.ToList();
            if (saved.Count == 0)
            {
                Respond("Cleanup rule is not set. Ask an admin or specify subtypeIds: !gm cleanup list SubtypeA,SubtypeB");
                return false;
            }

            rule = saved;
            return true;
        }

        private static bool TryParseHours(string raw, out double hours)
        {
            hours = 0;
            if (!double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
                return false;

            return !double.IsNaN(hours) && !double.IsInfinity(hours) && hours >= 0 && hours <= MaxDelayHours;
        }

        [Command("gm cleanup help", "Grid cleanup: help")]
        [Permission(MyPromoteLevel.None)]
        public void CleanupHelp()
        {
            // Консоль (Player == null) видит админскую справку.
            var player = Context?.Player;
            bool isAdmin = player == null || player.PromoteLevel >= MyPromoteLevel.Admin;

            if (!isAdmin)
            {
                Respond("Grid cleanup commands:");
                Respond("  !gm cleanup my");
                Respond("    Show your grids that are marked for removal");
                Respond("  !gm cleanup deleted");
                Respond("    Show your grids that were deleted in the last cleanup");
                return;
            }

            Respond("Grid cleanup commands (subtypeIds: comma separated, no spaces):");
            Respond("  !gm cleanup list [subtypes]");
            Respond("    List grids that satisfy / do not satisfy the rule (and NPC scrap). If subtypes are given, they become the active rule");
            Respond("  !gm cleanup warn");
            Respond("    Send a warning to every online player who owns grids marked for removal (uses the saved rule)");
            Respond("  !gm cleanup delete");
            Respond("    Dry run: shows what would be deleted (uses the saved rule)");
            Respond("  !gm cleanup delete confirm <hours>");
            Respond("    Deletes grids that do not satisfy the rule and NPC scrap (<= 2 blocks, no power). 0 - right now, 24 - in 24 hours. " +
                    "If the server is offline at that moment, nothing is deleted");
            Respond("  !gm cleanup scrap [confirm]");
            Respond("    Delete only NPC scrap (NPC grids with <= 2 blocks and no power), right now. Without 'confirm' - dry run");
            Respond("  !gm cleanup cancel");
            Respond("    Cancel the scheduled deletion");
            Respond("  !gm cleanup my");
            Respond("    Show your own grids that are marked for removal");
            Respond("  !gm cleanup deleted");
            Respond("    Show your own grids that were deleted in the last cleanup");
            Respond("  !gm cleanup deletedof <nick>");
            Respond("    Show any player's grids that were deleted in the last cleanup (nick: exact or part of it; use quotes if it has spaces)");
        }

        [Command("gm cleanup list", "Admin: list grids that satisfy / do not satisfy the cleanup rule")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanupList(string subtypes = null)
        {
            List<string> rule;
            if (!TryResolveRule(subtypes, out rule))
                return;

            var groups = GridCleanupManager.Analyze(rule, logSkipped: true);
            Respond(GridCleanupManager.BuildAdminList(rule, groups));
        }

        [Command("gm cleanup my", "Show your grids that are marked for removal")]
        [Permission(MyPromoteLevel.None)]
        public void CleanupMy()
        {
            var player = Context?.Player;
            if (player == null)
            {
                Respond("This command can only be used by a player in game.");
                return;
            }

            var rule = GridCleanupManager.Rule.ToList();
            if (rule.Count == 0)
            {
                Respond("No cleanup rule is active right now.");
                return;
            }

            var groups = GridCleanupManager.Analyze(rule);
            Respond(GridCleanupManager.BuildPlayerList(player.IdentityId, rule, groups));
        }

        [Command("gm cleanup deleted", "Show your grids that were deleted in the last cleanup")]
        [Permission(MyPromoteLevel.None)]
        public void CleanupDeleted()
        {
            var player = Context?.Player;
            if (player == null)
            {
                Respond("This command can only be used by a player in game.");
                return;
            }

            Respond(GridCleanupManager.BuildDeletedList(player.IdentityId));
        }

        [Command("gm cleanup deletedof", "Admin: show a player's grids deleted in the last cleanup. Usage: deletedof <nick>")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanupDeletedOf(string nick)
        {
            long identityId;
            string error;
            if (!GridCleanupManager.TryFindPlayerByNick(nick, out identityId, out error))
            {
                Respond(error);
                return;
            }

            Respond(GridCleanupManager.BuildDeletedList(identityId, GridCleanupManager.OwnerLabel(identityId)));
        }

        [Command("gm cleanup warn", "Admin: warn all players about their grids marked for removal")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanupWarn()
        {
            // Правило всегда берётся из сохранённого (как в !gm cleanup my)
            List<string> rule;
            if (!TryResolveRule(null, out rule))
                return;

            var groups = GridCleanupManager.Analyze(rule, logSkipped: true);

            // NPC-мусор не в счёт: предупреждать о нём некого.
            if (!groups.Any(g => !g.Satisfies && !g.IsNpcScrap))
            {
                Respond("No grids to warn about: every player grid satisfies the rule.");
                return;
            }

            var res = GridCleanupManager.SendWarnings(rule, groups);
            string offline = res.OfflineOwners.Count > 0
                ? $" Offline owners (not notified): {string.Join(", ", res.OfflineOwners)}."
                : "";
            Respond($"Warnings sent to {res.Notified} online player(s).{offline}");
        }

        [Command("gm cleanup scrap", "Admin: delete only NPC scrap grids (<= 2 blocks, no power). Add 'confirm' to execute")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanupScrap(string confirm = null)
        {
            if (confirm != null && !string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase))
            {
                Respond($"Unknown argument '{confirm}'. Usage: !gm cleanup scrap  |  !gm cleanup scrap confirm");
                return;
            }

            // NPC scrap does not depend on the subtype rule, so an empty rule is fine here.
            // Only the scrap groups are passed on - player groups must never reach DeleteUnsatisfied.
            var groups = GridCleanupManager.Analyze(new List<string>(), logSkipped: true);
            var scrap = groups.Where(g => g.IsNpcScrap).ToList();

            if (scrap.Count == 0)
            {
                Respond($"No NPC scrap found (NPC grids with <= {GridCleanupManager.MaxScrapBlocks} blocks and no power).");
                return;
            }

            if (confirm == null)
            {
                Respond("DRY RUN:\n" + GridCleanupManager.BuildScrapList(scrap) +
                        "Use '!gm cleanup scrap confirm' to delete them.");
                return;
            }

            var res = GridCleanupManager.DeleteUnsatisfied(scrap);
            Respond($"Deleted {res.GridsDeleted} NPC scrap grid(s) in {res.GroupsDeleted} group(s).");
        }

        [Command("gm cleanup cancel", "Admin: cancel the scheduled grid deletion")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanupCancel()
        {
            var pending = GridCleanupManager.Pending;
            if (pending == null)
            {
                Respond("No deletion is scheduled.");
                return;
            }

            string when = GridCleanupManager.DescribeDue(pending.DueUtc);
            GridCleanupManager.CancelScheduled();
            Respond($"Scheduled deletion cancelled (was: {when}).");
        }

        [Command("gm cleanup delete", "Admin: delete grids that do not satisfy the rule. Usage: delete [confirm <hours>]")]
        [Permission(MyPromoteLevel.Admin)]
        public void CleanupDelete(string confirm = null, string hours = null)
        {
            // 1) Разбор аргументов. Без аргументов — холостой прогон.
            //    С confirm обязателен hours: 0 — удалить сейчас, N — через N часов.
            bool confirmed = false;
            double delayHours = 0;

            if (confirm != null)
            {
                if (!string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase))
                {
                    Respond($"Unknown argument '{confirm}'. Usage: !gm cleanup delete  |  !gm cleanup delete confirm <hours>");
                    return;
                }

                if (string.IsNullOrWhiteSpace(hours))
                {
                    Respond("'hours' is required after 'confirm'. Examples: '!gm cleanup delete confirm 0' - delete now, " +
                            "'!gm cleanup delete confirm 24' - delete in 24 hours.");
                    return;
                }

                if (!TryParseHours(hours, out delayHours))
                {
                    Respond($"Invalid hours '{hours}'. Use a number from 0 to {MaxDelayHours:0}, e.g. 0 or 24.");
                    return;
                }

                confirmed = true;
            }

            // 2) Правило — только сохранённое (как в !gm cleanup my)
            List<string> rule;
            if (!TryResolveRule(null, out rule))
                return;

            var pending = GridCleanupManager.Pending;
            var groups = GridCleanupManager.Analyze(rule, logSkipped: true);
            var remove = groups.Where(g => !g.Satisfies).ToList();
            var removePlayers = remove.Where(g => !g.IsNpcScrap).ToList();
            var removeScrap = remove.Where(g => g.IsNpcScrap).ToList();
            int keep = groups.Count(g => g.Satisfies);

            if (remove.Count == 0)
            {
                Respond("Nothing to delete: every player grid satisfies the rule and there is no NPC scrap.");
                return;
            }

            int playerGrids = removePlayers.Sum(g => g.Grids.Count);
            int scrapGrids = removeScrap.Sum(g => g.Grids.Count);

            // 3) Холостой прогон
            if (!confirmed)
            {
                string warning = keep == 0 && removePlayers.Count > 0
                    ? "\nWARNING: no player grid satisfies this rule at all - double check the subtypeIds!"
                    : "";
                string scheduled = pending != null
                    ? $"\nNOTE: a deletion is already scheduled: {GridCleanupManager.DescribeDue(pending.DueUtc)}."
                    : "";

                Respond($"DRY RUN: would delete {playerGrids} player grid(s) in {removePlayers.Count} group(s) " +
                        $"and {scrapGrids} NPC scrap grid(s) in {removeScrap.Count} group(s); " +
                        $"{keep} player group(s) kept.{warning}{scheduled}\n" +
                        $"Rule: {GridCleanupManager.RequiredBlockLabel}.\n" +
                        "Use '!gm cleanup list' to see them, then '!gm cleanup delete confirm 0' (now) " +
                        "or '!gm cleanup delete confirm <hours>' (later) to execute.");
                return;
            }

            // 4) Отложенное удаление
            if (delayHours > 0)
            {
                if (pending != null)
                {
                    Respond($"A deletion is already scheduled: {GridCleanupManager.DescribeDue(pending.DueUtc)}. " +
                            "Use '!gm cleanup cancel' first.");
                    return;
                }

                string by = Context?.Player?.DisplayName ?? "console";
                var sched = GridCleanupManager.Schedule(rule, delayHours, by);

                Respond($"Deletion scheduled: {GridCleanupManager.DescribeDue(sched.DueUtc)}.\n" +
                        $"Rule: {GridCleanupManager.RequiredBlockLabel}. Right now {playerGrids} player grid(s) and {scrapGrids} NPC scrap grid(s) match; " +
                        "the list is recalculated at execution time. If the server is offline at that moment, nothing will be deleted.\n" +
                        "Use '!gm cleanup warn' to notify players, '!gm cleanup cancel' to cancel.");
                return;
            }

            // 5) Удаление сейчас (hours = 0). Ранее запланированное удаление становится не нужным.
            bool hadPending = GridCleanupManager.CancelScheduled();

            var res = GridCleanupManager.DeleteUnsatisfied(groups);
            string replaced = hadPending ? " The previously scheduled deletion was cancelled." : "";
            Respond($"Deleted {res.GridsDeleted} grid(s) in {res.GroupsDeleted} group(s) (NPC scrap: {res.ScrapGridsDeleted}). " +
                    $"Skipped {res.GroupsSkippedPilot} group(s) with a pilot inside.{replaced}");
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Logging;
using VRage.Game.ModAPI;

namespace TorchPlugin
{
    /// <summary>
    /// Группа механически связанных гридов (грид + роторные/поршневые подгриды).
    /// Проверка и удаление выполняются на уровне группы, чтобы не снести
    /// голову ротора / поршня только потому, что в ней нет нужного блока.
    /// </summary>
    public class CleanupGroup
    {
        public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();

        /// <summary>
        /// Владельцы группы (BigOwners ∪ SmallOwners ∪ OwnerId блоков).
        /// Если владельцев нет вообще, а блоки построены игроками (BuiltBy) —
        /// здесь лежат строители, и выставлен OwnersFromBuiltBy.
        /// </summary>
        public readonly HashSet<long> Owners = new HashSet<long>();

        /// <summary>Все ненулевые BuiltBy блоков группы.</summary>
        public readonly HashSet<long> Builders = new HashSet<long>();

        public bool OwnersFromBuiltBy;

        /// <summary>NPC-группа из мелких обесточенных гридов: удаляется независимо от правила.</summary>
        public bool IsNpcScrap;

        public bool Satisfies;
        public string MatchedSubtype;
        public bool HasPilot;
        public int TotalBlocks;
    }

    /// <summary>Один удалённый грид (для команды !gm cleanup deleted).</summary>
    public class DeletedGridRecord
    {
        public string Name { get; set; }
        public long EntityId { get; set; }
        public int Blocks { get; set; }
        public List<long> OwnerIds { get; set; } = new List<long>();
    }

    /// <summary>Отложенное удаление, запланированное командой !gm cleanup delete confirm &lt;hours&gt;.</summary>
    public class ScheduledDeletion
    {
        public DateTime ScheduledUtc { get; set; }
        public DateTime DueUtc { get; set; }
        public string RequestedBy { get; set; }

        /// <summary>Снимок правила на момент планирования: именно оно применится при удалении.</summary>
        public List<string> Rule { get; set; } = new List<string>();
    }

    /// <summary>Результат последнего подтверждённого удаления.</summary>
    public class DeletedRun
    {
        public DateTime RunUtc { get; set; }
        public List<DeletedGridRecord> Grids { get; set; } = new List<DeletedGridRecord>();

        /// <summary>Игроки (identityId), которым уже отправлено уведомление об этой чистке после захода.</summary>
        public List<long> NotifiedIds { get; set; } = new List<long>();
    }

    public static class GridCleanupManager
    {
        private const string RuleFileName = "GridCleanupRule.json";
        private const string DeletedFileName = "GridCleanupDeleted.json";
        private const string ScheduledFileName = "GridCleanupScheduled.json";

        /// <summary>NPC-группа с суммарным числом блоков не больше этого и без энергии удаляется.</summary>
        public const int MaxScrapBlocks = 2;

        /// <summary>
        /// Название обязательного блока, которое показывается игрокам в сообщениях.
        /// В логике правила не используется — правило по-прежнему задаётся списком subtypeId.
        /// </summary>
        public const string RequiredBlockLabel = "Owner Plate";

        private static IPluginLogger Log => Plugin.Instance?.Log;

        // ───────────────────────── Правило (список subtypeId) ─────────────────────────

        private static List<string> _rule;

        public static IReadOnlyList<string> Rule
        {
            get
            {
                if (_rule == null)
                    LoadRule();
                return _rule;
            }
        }

        private static string RulePath => Path.Combine(Plugin.Instance.StoragePath, RuleFileName);

        private static void LoadRule()
        {
            _rule = new List<string>();
            try
            {
                var path = RulePath;
                if (!File.Exists(path))
                    return;

                var list = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                if (list != null)
                    _rule = list;
            }
            catch (Exception e)
            {
                Log?.Error(e, "GridCleanup: failed to load rule");
            }
        }

        public static void SetRule(List<string> subtypes)
        {
            _rule = subtypes ?? new List<string>();
            try
            {
                File.WriteAllText(RulePath, JsonConvert.SerializeObject(_rule, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log?.Error(e, "GridCleanup: failed to save rule");
            }
        }

        /// <summary>"A,B;C" -> ["A","B","C"] (без пустых и дублей, регистр не важен).</summary>
        public static List<string> ParseSubtypes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Возвращает subtypeId, которых нет ни в одном определении кубического блока.</summary>
        public static List<string> FindUnknownSubtypes(IEnumerable<string> subtypes)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in MyDefinitionManager.Static.GetDefinitionsOfType<MyCubeBlockDefinition>())
                known.Add(def.Id.SubtypeName ?? string.Empty);

            return subtypes.Where(s => !known.Contains(s)).ToList();
        }

        // ───────────────────────── Журнал удалённых гридов ─────────────────────────

        private static DeletedRun _lastRun;
        private static bool _lastRunLoaded;

        private static string DeletedPath => Path.Combine(Plugin.Instance.StoragePath, DeletedFileName);

        /// <summary>Последнее подтверждённое удаление, затронувшее гриды игроков (или null).</summary>
        public static DeletedRun LastRun
        {
            get
            {
                if (!_lastRunLoaded)
                {
                    _lastRunLoaded = true;
                    try
                    {
                        var path = DeletedPath;
                        if (File.Exists(path))
                            _lastRun = JsonConvert.DeserializeObject<DeletedRun>(File.ReadAllText(path));
                    }
                    catch (Exception e)
                    {
                        Log?.Error(e, "GridCleanup: failed to load deleted-grids journal");
                    }
                }
                return _lastRun;
            }
        }

        private static void SaveLastRun(DeletedRun run)
        {
            _lastRun = run;
            _lastRunLoaded = true;
            try
            {
                File.WriteAllText(DeletedPath, JsonConvert.SerializeObject(run, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log?.Error(e, "GridCleanup: failed to save deleted-grids journal");
            }
        }

        /// <summary>
        /// Ищет игрока по нику: сначала точное совпадение (без учёта регистра), затем по вхождению.
        /// true — найден ровно один игрок; иначе error объясняет, что не так.
        /// </summary>
        public static bool TryFindPlayerByNick(string nick, out long identityId, out string error)
        {
            identityId = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(nick))
            {
                error = "Player nick is empty.";
                return false;
            }

            nick = nick.Trim();

            // identityId -> ник: все identity мира + кэш players.json (там есть и давно не заходившие)
            var names = new Dictionary<long, string>();

            var identities = MySession.Static?.Players?.GetAllIdentities();
            if (identities != null)
            {
                foreach (var identity in identities)
                {
                    if (!string.IsNullOrEmpty(identity.DisplayName))
                        names[identity.IdentityId] = identity.DisplayName;
                }
            }

            var cache = TorchPlayersCache.ByIdentity;
            if (cache != null)
            {
                foreach (var kv in cache)
                {
                    if (!names.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value.Player))
                        names[kv.Key] = kv.Value.Player;
                }
            }

            // Только реальные игроки — чтобы NPC с похожим именем не мешали.
            var players = names.Where(kv => IsPlayerIdentity(kv.Key)).ToList();

            var exact = players.Where(kv => string.Equals(kv.Value, nick, StringComparison.OrdinalIgnoreCase)).ToList();
            var found = exact.Count > 0
                ? exact
                : players.Where(kv => kv.Value.IndexOf(nick, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (found.Count == 1)
            {
                identityId = found[0].Key;
                return true;
            }

            if (found.Count == 0)
            {
                error = $"Player '{nick}' not found.";
                return false;
            }

            const int maxShown = 10;
            string list = string.Join(", ", found.Take(maxShown).Select(kv => OwnerLabel(kv.Key)));
            if (found.Count > maxShown)
                list += $" and {found.Count - maxShown} more";

            error = $"Several players match '{nick}': {list}. Specify the exact nick.";
            return false;
        }

        /// <summary>
        /// Список удалённых при последней чистке гридов игрока.
        /// playerLabel == null — текст «для себя» (команда игрока), иначе админский вариант с EntityId.
        /// </summary>
        public static string BuildDeletedList(long identityId, string playerLabel = null)
        {
            var run = LastRun;
            if (run == null || run.Grids == null || run.Grids.Count == 0)
                return "No player grids have been deleted by cleanup yet.";

            string when = run.RunUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";
            var mine = run.Grids.Where(g => g.OwnerIds != null && g.OwnerIds.Contains(identityId)).ToList();
            if (mine.Count == 0)
                return playerLabel == null
                    ? $"None of your grids were deleted in the last cleanup ({when})."
                    : $"None of {playerLabel}'s grids were deleted in the last cleanup ({when}).";

            var sb = new StringBuilder();
            sb.AppendLine(playerLabel == null
                ? $"Your grids deleted in the last cleanup ({when}): {mine.Count}"
                : $"Grids of {playerLabel} deleted in the last cleanup ({when}): {mine.Count}");

            foreach (var g in mine)
            {
                sb.AppendLine(playerLabel == null
                    ? $"  - {g.Name} ({g.Blocks} blocks)"
                    : $"  - {g.Name} ({g.EntityId}, {g.Blocks} blocks)");
            }

            return sb.ToString();
        }

        // ───────────────────────── Отложенное удаление ─────────────────────────

        private static ScheduledDeletion _pending;
        private static bool _pendingLoaded;

        // Первый Tick после старта процесса проверяет, не истёк ли срок, пока сервер был выключен.
        private static bool _startupChecked;
        private static DateTime _nextCheckUtc = DateTime.MinValue;

        private static string ScheduledPath => Path.Combine(Plugin.Instance.StoragePath, ScheduledFileName);

        /// <summary>Запланированное удаление или null.</summary>
        public static ScheduledDeletion Pending
        {
            get
            {
                if (!_pendingLoaded)
                {
                    _pendingLoaded = true;
                    try
                    {
                        var path = ScheduledPath;
                        if (File.Exists(path))
                            _pending = JsonConvert.DeserializeObject<ScheduledDeletion>(File.ReadAllText(path));
                    }
                    catch (Exception e)
                    {
                        Log?.Error(e, "GridCleanup: failed to load scheduled deletion");
                    }
                }
                return _pending;
            }
        }

        public static ScheduledDeletion Schedule(IEnumerable<string> rule, double hours, string requestedBy)
        {
            var now = DateTime.UtcNow;
            var p = new ScheduledDeletion
            {
                ScheduledUtc = now,
                DueUtc = now.AddHours(hours),
                RequestedBy = requestedBy,
                Rule = rule.ToList()
            };

            _pending = p;
            _pendingLoaded = true;
            _startupChecked = true;

            try
            {
                File.WriteAllText(ScheduledPath, JsonConvert.SerializeObject(p, Formatting.Indented));
            }
            catch (Exception e)
            {
                Log?.Error(e, "GridCleanup: failed to save scheduled deletion");
            }

            Log?.Info($"GridCleanup: deletion scheduled for {p.DueUtc:yyyy-MM-dd HH:mm:ss} UTC by '{requestedBy}', rule: {string.Join(", ", p.Rule)}");
            return p;
        }

        /// <summary>Отменяет запланированное удаление. true, если оно было.</summary>
        public static bool CancelScheduled()
        {
            bool had = Pending != null;
            ClearPending();
            if (had)
                Log?.Info("GridCleanup: scheduled deletion cancelled");
            return had;
        }

        private static void ClearPending()
        {
            _pending = null;
            _pendingLoaded = true;
            try
            {
                if (File.Exists(ScheduledPath))
                    File.Delete(ScheduledPath);
            }
            catch (Exception e)
            {
                Log?.Error(e, "GridCleanup: failed to delete scheduled-deletion file");
            }
        }

        public static bool SameRule(IEnumerable<string> a, IEnumerable<string> b)
        {
            return new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
        }

        /// <summary>"2026-10-26 13:30 UTC (in 23h 41m)"</summary>
        public static string DescribeDue(DateTime dueUtc)
        {
            var left = dueUtc - DateTime.UtcNow;
            if (left < TimeSpan.Zero)
                left = TimeSpan.Zero;

            return $"{dueUtc:yyyy-MM-dd HH:mm} UTC (in {(int)left.TotalHours}h {left.Minutes}m)";
        }

        /// <summary>
        /// Вызывать каждый тик из Plugin.CustomUpdate (главный поток).
        /// Выполняет запланированное удаление, когда срок наступил.
        /// Если срок истёк, пока сервер был выключен, — удаление отменяется, а не выполняется с опозданием.
        /// </summary>
        public static void Tick()
        {
            var session = MySession.Static;
            if (session == null || !session.Ready || Plugin.Instance == null)
                return;

            var now = DateTime.UtcNow;
            if (now < _nextCheckUtc)
                return;
            _nextCheckUtc = now.AddSeconds(1);

            try
            {
                try
                {
                    TickJoinNotices(now);
                }
                catch (Exception e)
                {
                    Log?.Error(e, "GridCleanup: join notices failed");
                }

                var pending = Pending;

                if (!_startupChecked)
                {
                    _startupChecked = true;
                    if (pending != null && pending.DueUtc <= now)
                    {
                        Log?.Warning($"GridCleanup: scheduled deletion (due {pending.DueUtc:yyyy-MM-dd HH:mm:ss} UTC) was missed " +
                                     "because the server was offline - cancelled, nothing deleted");
                        ClearPending();
                        return;
                    }
                }

                if (pending == null || now < pending.DueUtc)
                    return;

                ExecuteScheduled(pending);
            }
            catch (Exception e)
            {
                Log?.Error(e, "GridCleanup: scheduled deletion tick failed");
            }
        }

        // ───────────────────────── Уведомление после захода ─────────────────────────

        private static readonly TimeSpan JoinNoticeDelay = TimeSpan.FromMinutes(1);

        // identityId -> момент, когда мы впервые увидели игрока онлайн (сбрасывается при выходе)
        private static readonly Dictionary<long, DateTime> _joinedAt = new Dictionary<long, DateTime>();
        private static readonly List<IMyPlayer> _playersBuffer = new List<IMyPlayer>();

        /// <summary>
        /// Максимум символов в одном чат-сообщении. Строки объединяются в одно сообщение, пока
        /// влезают; дальше начинается следующее. Значение — консервативная оценка, при необходимости меняется здесь.
        /// </summary>
        private const int MaxChatMessageChars = 1000;

        /// <summary>IdentityId всех живых (не NPC, не боты) игроков, онлайн прямо сейчас.</summary>
        private static HashSet<long> GetOnlineHumanIds()
        {
            var ids = new HashSet<long>();
            _playersBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(_playersBuffer);

            foreach (var p in _playersBuffer)
            {
                long id = p.IdentityId;
                if (id <= 0 || p.IsBot)
                    continue;

                if (MySession.Static.Players.IdentityIsNpc(id))
                    continue;

                ids.Add(id);
            }

            return ids;
        }

        /// <summary>Отправляет текст игроку: строки идут одним сообщением, пока не превышен лимит длины.</summary>
        private static void SendChatText(long identityId, string text, string font)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                if (sb.Length > 0 && sb.Length + 1 + line.Length > MaxChatMessageChars)
                {
                    MyVisualScriptLogicProvider.SendChatMessage(sb.ToString(), "GridCleanup", identityId, font);
                    sb.Clear();
                }

                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(line);
            }

            if (sb.Length > 0)
                MyVisualScriptLogicProvider.SendChatMessage(sb.ToString(), "GridCleanup", identityId, font);
        }

        /// <summary>Отправляет игроку итог чистки (как !gm cleanup deleted) и помечает его уведомлённым.</summary>
        private static void SendDeletedNotice(long identityId, DeletedRun run)
        {
            SendChatText(identityId, BuildDeletedList(identityId), "White");

            if (run.NotifiedIds == null)
                run.NotifiedIds = new List<long>();

            if (!run.NotifiedIds.Contains(identityId))
                run.NotifiedIds.Add(identityId);

            Log?.Info($"GridCleanup: sent deleted-grids notice to {OwnerLabel(identityId)}");
        }

        /// <summary>Сразу после чистки уведомляет всех, кто сейчас онлайн. Остальные получат сообщение после захода.</summary>
        private static void NotifyOnlinePlayers(DeletedRun run)
        {
            var online = GetOnlineHumanIds();
            foreach (var id in online)
                SendDeletedNotice(id, run);

            if (online.Count > 0)
                SaveLastRun(run);
        }

        /// <summary>
        /// Через минуту после захода игрока отправляет ему то же, что показывает !gm cleanup deleted.
        /// Один раз на каждую чистку для каждого игрока (в том числе если его гриды не удалялись).
        /// Кто был онлайн во время чистки, получил сообщение сразу (NotifyOnlinePlayers) и в NotifiedIds
        /// уже записан — при перезаходе повторно не уведомляется.
        /// </summary>
        private static void TickJoinNotices(DateTime now)
        {
            var online = GetOnlineHumanIds();
            foreach (var id in online)
            {
                if (!_joinedAt.ContainsKey(id))
                    _joinedAt[id] = now;
            }

            foreach (var id in _joinedAt.Keys.Where(k => !online.Contains(k)).ToList())
                _joinedAt.Remove(id);

            var run = LastRun;
            if (run == null || run.Grids == null || run.Grids.Count == 0)
                return;

            if (run.NotifiedIds == null)
                run.NotifiedIds = new List<long>();

            bool changed = false;
            foreach (var kv in _joinedAt)
            {
                long id = kv.Key;

                if (now - kv.Value < JoinNoticeDelay)
                    continue;

                if (run.NotifiedIds.Contains(id))
                    continue;

                SendDeletedNotice(id, run);
                changed = true;
            }

            if (changed)
                SaveLastRun(run);
        }

        private static void ExecuteScheduled(ScheduledDeletion p)
        {
            // Сначала снимаем план: повторного запуска быть не должно ни при каких ошибках.
            ClearPending();

            var rule = p.Rule ?? new List<string>();
            if (rule.Count == 0)
            {
                Log?.Warning("GridCleanup: scheduled deletion has an empty rule - cancelled");
                return;
            }

            var unknown = FindUnknownSubtypes(rule);
            if (unknown.Count > 0)
            {
                Log?.Warning($"GridCleanup: scheduled deletion cancelled, unknown subtypeId(s): {string.Join(", ", unknown)}");
                return;
            }

            Log?.Info($"GridCleanup: executing scheduled deletion (requested by '{p.RequestedBy}'), rule: {string.Join(", ", rule)}");

            var groups = Analyze(rule, logSkipped: true);
            var res = DeleteUnsatisfied(groups);

            Log?.Info($"GridCleanup: scheduled deletion done: {res.GridsDeleted} grid(s) in {res.GroupsDeleted} group(s), " +
                      $"NPC scrap: {res.ScrapGridsDeleted}, skipped (pilot): {res.GroupsSkippedPilot}");
        }

        // ───────────────────────── Определение «гридов игроков» ─────────────────────────

        /// <summary>
        /// Строгая проверка: identity — живой игрок (не NPC, не NPC-фракция,
        /// известен SteamId). Любая неопределённость → false (грид не трогаем).
        /// </summary>
        public static bool IsPlayerIdentity(long identityId)
        {
            return NotPlayerReason(identityId) == null;
        }

        /// <summary>null, если identity — реальный игрок; иначе причина, почему нет.</summary>
        public static string NotPlayerReason(long identityId)
        {
            if (identityId <= 0)
                return "invalid identity id";

            var players = MySession.Static?.Players;
            if (players == null)
                return "players collection unavailable";

            if (players.IdentityIsNpc(identityId))
                return "NPC identity";

            var faction = MySession.Static.Factions?.TryGetPlayerFaction(identityId);
            if (faction != null && Plugin.Instance != null && Plugin.Instance.IsNpcFaction(faction))
                return $"member of NPC faction '{faction.Name}' [{faction.Tag}]";

            if (players.TryGetSteamId(identityId) != 0)
                return null;

            var cached = Plugin.GetPlayerSteamIdByIdentity(identityId);
            if (cached.HasValue && cached.Value != 0)
                return null;

            return "no SteamId known";
        }

        /// <summary>
        /// Положительное определение NPC (в отличие от NotPlayerReason, «неизвестный SteamId»
        /// сюда не попадает): NPC-identity или член NPC-фракции.
        /// </summary>
        public static bool IsNpcIdentity(long identityId)
        {
            if (identityId <= 0)
                return false;

            var players = MySession.Static?.Players;
            if (players == null)
                return false;

            if (players.IdentityIsNpc(identityId))
                return true;

            var faction = MySession.Static.Factions?.TryGetPlayerFaction(identityId);
            return faction != null && Plugin.Instance != null && Plugin.Instance.IsNpcFaction(faction);
        }

        private static void CollectGridOwners(MyCubeGrid grid, HashSet<long> owners)
        {
            if (grid.BigOwners != null)
                foreach (var o in grid.BigOwners)
                    owners.Add(o);

            if (grid.SmallOwners != null)
                foreach (var o in grid.SmallOwners)
                    owners.Add(o);
        }

        /// <summary>
        /// Есть ли у грида доступная электрическая мощность (та же проверка, что в FindGrids).
        /// known=false — состояние определить не удалось (тогда грид не удаляем).
        /// </summary>
        private static bool HasElectricity(MyCubeGrid grid, out bool known)
        {
            known = false;
            try
            {
                IMyCubeGrid ig = grid;
                var rd = ig.ResourceDistributor;
                if (rd == null)
                    return false;

                known = true;
                return rd.MaxAvailableResourceByType(MyResourceDistributorComponent.ElectricityId) > 0;
            }
            catch (Exception e)
            {
                Log?.Error(e, $"GridCleanup: failed to read power of grid {grid.EntityId}");
                return false;
            }
        }

        /// <summary>
        /// Пытается пометить NPC-группу как «мусор» (мало блоков, нет энергии).
        /// Возвращает null, если пометила; иначе причину, почему группа не подходит.
        /// </summary>
        private static string TryMarkNpcScrap(CleanupGroup cg)
        {
            if (cg.TotalBlocks > MaxScrapBlocks)
                return $"NPC group with {cg.TotalBlocks} blocks (> {MaxScrapBlocks}) - not scrap";

            if (cg.HasPilot)
                return "NPC scrap candidate has a pilot inside";

            foreach (var grid in cg.Grids)
            {
                bool known;
                if (HasElectricity(grid, out known))
                    return $"NPC scrap candidate '{grid.DisplayName}' ({grid.EntityId}) has electricity";

                if (!known)
                    return $"NPC scrap candidate '{grid.DisplayName}' ({grid.EntityId}): power state unknown";
            }

            cg.IsNpcScrap = true;
            cg.Satisfies = false;
            cg.MatchedSubtype = null;
            return null;
        }

        /// <summary>
        /// Анализ мира. В результат попадают:
        ///  1) группы гридов игроков:
        ///     - нет NPC-гридов (IsNpcSpawnedGrid) вперемешку с обычными;
        ///     - владельцы (BigOwners ∪ SmallOwners ∪ OwnerId всех блоков) непусты и ВСЕ — реальные игроки;
        ///     - если владельцев нет совсем (грид «заклеймлен» — блоки с BuiltBy игрока),
        ///       то в качестве владельцев берутся строители (BuiltBy), и ВСЕ они должны быть игроками.
        ///     Satisfies = хотя бы в одном гриде группы есть блок с одним из subtypeId.
        ///  2) NPC-«мусор»: все гриды группы NPC, суммарно не больше MaxScrapBlocks блоков, без энергии.
        ///     Такие группы помечены IsNpcScrap, Satisfies = false (правило subtypeId к ним не применяется).
        /// </summary>
        public static List<CleanupGroup> Analyze(IEnumerable<string> subtypes, bool logSkipped = false)
        {
            var wanted = new HashSet<string>(subtypes, StringComparer.OrdinalIgnoreCase);
            var result = new List<CleanupGroup>();
            var skipLog = new StringBuilder();

            foreach (var group in MyCubeGridGroups.Static.Mechanical.Groups)
            {
                var cg = new CleanupGroup();
                bool allNpc = true;
                bool anyNpcSpawned = false;

                foreach (var node in group.Nodes)
                {
                    var grid = node.NodeData;
                    if (grid == null || grid.Closed || grid.MarkedForClose || grid.IsPreview || grid.Physics == null)
                        continue;

                    cg.Grids.Add(grid);
                    cg.TotalBlocks += grid.BlocksCount;

                    var gridOwners = new HashSet<long>();
                    var gridBuilders = new HashSet<long>();
                    CollectGridOwners(grid, gridOwners);

                    foreach (var slim in grid.GetBlocks())
                    {
                        if (slim.OwnerId != 0)
                            gridOwners.Add(slim.OwnerId);

                        if (slim.BuiltBy != 0)
                        {
                            cg.Builders.Add(slim.BuiltBy);
                            gridBuilders.Add(slim.BuiltBy);
                        }

                        var fat = slim.FatBlock;
                        if (fat != null)
                        {
                            if (fat.OwnerId != 0)
                                gridOwners.Add(fat.OwnerId);

                            var ctrl = fat as MyShipController;
                            if (ctrl != null && ctrl.Pilot != null)
                                cg.HasPilot = true;
                        }

                        if (!cg.Satisfies)
                        {
                            var subtype = slim.BlockDefinition?.Id.SubtypeName;
                            if (subtype != null && wanted.Contains(subtype))
                            {
                                cg.Satisfies = true;
                                cg.MatchedSubtype = subtype;
                            }
                        }
                    }

                    cg.Owners.UnionWith(gridOwners);

                    if (grid.IsNpcSpawnedGrid)
                        anyNpcSpawned = true;

                    // NPC-грид: спавн NPC-системой, либо все владельцы NPC, либо владельцев нет совсем,
                    // но все блоки построены NPC (BuiltBy) — так выглядят «осиротевшие» NPC-аванпосты.
                    bool gridIsNpc = grid.IsNpcSpawnedGrid ||
                                     (gridOwners.Count > 0
                                         ? gridOwners.All(IsNpcIdentity)
                                         : gridBuilders.Count > 0 && gridBuilders.All(IsNpcIdentity));
                    if (!gridIsNpc)
                        allNpc = false;
                }

                string skipReason = null;

                if (cg.Grids.Count == 0)
                {
                    skipReason = "no valid grids (closed / preview / no physics)";
                }
                else if (allNpc)
                {
                    skipReason = TryMarkNpcScrap(cg);
                }
                else if (anyNpcSpawned)
                {
                    skipReason = "mixed group: NPC-spawned grid(s) together with non-NPC grid(s)";
                }
                else if (cg.Owners.Count > 0)
                {
                    var bad = cg.Owners
                        .Select(o => new { Id = o, Reason = NotPlayerReason(o) })
                        .Where(x => x.Reason != null)
                        .ToList();

                    if (bad.Count > 0)
                        skipReason = "non-player owner(s): " +
                                     string.Join("; ", bad.Select(x => $"'{OwnerLabel(x.Id)}' - {x.Reason}"));
                }
                else if (cg.Builders.Count > 0)
                {
                    // Владельцев нет совсем — смотрим, кто строил блоки («Built by»).
                    var bad = cg.Builders
                        .Select(o => new { Id = o, Reason = NotPlayerReason(o) })
                        .Where(x => x.Reason != null)
                        .ToList();

                    if (bad.Count > 0)
                    {
                        skipReason = "no owners; non-player BuiltBy: " +
                                     string.Join("; ", bad.Select(x => $"'{OwnerLabel(x.Id)}' - {x.Reason}"));
                    }
                    else
                    {
                        cg.OwnersFromBuiltBy = true;
                        cg.Owners.UnionWith(cg.Builders);
                    }
                }
                else
                {
                    skipReason = "no owners and no BuiltBy (BigOwners, SmallOwners, block owners and BuiltBy are all empty)";
                }

                if (skipReason != null)
                {
                    if (logSkipped && cg.Grids.Count + group.Nodes.Count > 0)
                    {
                        var desc = new List<string>();
                        foreach (var node in group.Nodes)
                        {
                            var g = node.NodeData;
                            if (g == null || g.Closed || g.MarkedForClose || g.IsPreview)
                                continue;

                            string big = g.BigOwners == null ? "" : string.Join(", ", g.BigOwners.Select(OwnerLabel));
                            string small = g.SmallOwners == null ? "" : string.Join(", ", g.SmallOwners.Select(OwnerLabel));
                            string built = string.Join(", ", cg.Builders.Select(OwnerLabel));
                            desc.Add($"'{g.DisplayName}' ({g.EntityId}, {g.BlocksCount} blocks, npcSpawned={g.IsNpcSpawnedGrid}, " +
                                     $"BigOwners=[{big}], SmallOwners=[{small}], BuiltBy=[{built}])");
                        }

                        if (desc.Count > 0)
                            skipLog.AppendLine($"  SKIPPED: {string.Join(" + ", desc)} -> {skipReason}");
                    }
                    continue;
                }

                result.Add(cg);
            }

            if (logSkipped)
                Log?.Info($"GridCleanup: groups skipped (not treated as player grids):\n{skipLog}");

            // Группа считается подходящей и тогда, когда она физически связана
            // (коннектор / посадочная опора / merge-блок / подгрид) с группой,
            // в которой есть требуемый блок. Иначе пристыкованный к «нужной» базе
            // корабль без такого блока был бы удалён.
            var satisfyingGrids = new HashSet<long>(
                result.Where(g => g.Satisfies).SelectMany(g => g.Grids).Select(x => x.EntityId));

            if (satisfyingGrids.Count > 0)
            {
                var connected = new HashSet<long>();
                foreach (var pgroup in MyCubeGridGroups.Static.Physical.Groups)
                {
                    bool touches = false;
                    foreach (var node in pgroup.Nodes)
                    {
                        var g = node.NodeData;
                        if (g != null && satisfyingGrids.Contains(g.EntityId))
                        {
                            touches = true;
                            break;
                        }
                    }

                    if (!touches)
                        continue;

                    foreach (var node in pgroup.Nodes)
                    {
                        var g = node.NodeData;
                        if (g != null)
                            connected.Add(g.EntityId);
                    }
                }

                var promoted = new List<CleanupGroup>();
                foreach (var g in result.Where(g => !g.Satisfies && !g.IsNpcScrap))
                {
                    if (g.Grids.Any(x => connected.Contains(x.EntityId)))
                    {
                        g.Satisfies = true;
                        promoted.Add(g);
                    }
                }

                if (promoted.Count > 0)
                    Log?.Info($"GridCleanup: kept (connected to a grid with {RequiredBlockLabel}): " +
                              string.Join("; ", promoted.Select(g => GridNames(g, true))));
            }

            return result;
        }

        // ───────────────────────── Форматирование ─────────────────────────

        public static string OwnerLabel(long identityId)
        {
            string name = MySession.Static?.Players?.TryGetIdentity(identityId)?.DisplayName
                          ?? Plugin.GetPlayerNameByIdentity(identityId)
                          ?? identityId.ToString();

            var faction = MySession.Static?.Factions?.TryGetPlayerFaction(identityId);
            return faction != null ? $"{name} [{faction.Tag}]" : name;
        }

        private static string GridNames(CleanupGroup g, bool withIds)
        {
            return string.Join(", ", g.Grids.Select(x => withIds
                ? $"{x.DisplayName} ({x.EntityId}, {x.BlocksCount} blocks)"
                : $"{x.DisplayName} ({x.BlocksCount} blocks)"));
        }

        private static void AppendSection(StringBuilder sb, string title, List<CleanupGroup> groups, int cap, bool keepSection)
        {
            sb.AppendLine($"{title}: {groups.Count} group(s), {groups.Sum(g => g.Grids.Count)} grid(s)");

            int shown = 0;
            foreach (var g in groups)
            {
                if (shown >= cap)
                {
                    sb.AppendLine($"  ... and {groups.Count - cap} more (see server log)");
                    break;
                }

                string owners = g.Owners.Count > 0 ? string.Join(", ", g.Owners.Select(OwnerLabel)) : "none (NPC)";
                string extra = keepSection
                    ? (g.MatchedSubtype != null ? $" | has {RequiredBlockLabel}" : $" | kept (connected to a grid with {RequiredBlockLabel})")
                    : (g.HasPilot ? " | PILOT INSIDE (will be skipped)" : "");
                if (g.OwnersFromBuiltBy)
                    extra += " | no owners, matched by 'Built by'";

                sb.AppendLine($"  - {GridNames(g, true)} | owners: {owners}{extra}");
                shown++;
            }
        }

        public static string BuildAdminList(IReadOnlyCollection<string> subtypes, List<CleanupGroup> groups, bool logFull = true)
        {
            var keep = groups.Where(g => g.Satisfies).ToList();
            var remove = groups.Where(g => !g.Satisfies && !g.IsNpcScrap).ToList();
            var scrap = groups.Where(g => g.IsNpcScrap).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Grid cleanup rule: keep groups with >= 1 block of: {RequiredBlockLabel}");

            var pend = Pending;
            if (pend != null)
                sb.AppendLine($"Scheduled deletion: {DescribeDue(pend.DueUtc)} (requested by {pend.RequestedBy})");
            AppendSection(sb, "SATISFY (keep)", keep, 25, true);
            AppendSection(sb, "NOT SATISFY (to remove)", remove, 40, false);
            AppendSection(sb, $"NPC SCRAP (<= {MaxScrapBlocks} blocks, no power; to remove)", scrap, 25, false);

            if (logFull)
            {
                var full = new StringBuilder();
                AppendSection(full, "SATISFY (keep)", keep, int.MaxValue, true);
                AppendSection(full, "NOT SATISFY (to remove)", remove, int.MaxValue, false);
                AppendSection(full, "NPC SCRAP (to remove)", scrap, int.MaxValue, false);
                Log?.Info($"GridCleanup: full listing:\n{full}");
            }

            return sb.ToString();
        }

        public static string BuildScrapList(List<CleanupGroup> scrap, bool logFull = true)
        {
            var sb = new StringBuilder();
            AppendSection(sb, $"NPC SCRAP (<= {MaxScrapBlocks} blocks, no power)", scrap, 25, false);

            if (logFull)
            {
                var full = new StringBuilder();
                AppendSection(full, "NPC SCRAP", scrap, int.MaxValue, false);
                Log?.Info($"GridCleanup: NPC scrap listing:\n{full}");
            }

            return sb.ToString();
        }

        public static string BuildPlayerList(long identityId, IReadOnlyCollection<string> subtypes, List<CleanupGroup> groups)
        {
            var mine = groups.Where(g => !g.Satisfies && !g.IsNpcScrap && g.Owners.Contains(identityId)).ToList();

            var sb = new StringBuilder();
            if (mine.Count == 0)
            {
                sb.AppendLine("None of your grids are marked for removal.");
                return sb.ToString();
            }

            sb.AppendLine($"Your grids marked for removal ({mine.Sum(g => g.Grids.Count)}):");

            var pending = Pending;
            if (pending != null)
                sb.AppendLine($"Scheduled deletion: {DescribeDue(pending.DueUtc)}");

            sb.AppendLine($"To keep a grid, it must have at least one block of: {RequiredBlockLabel}");
            foreach (var g in mine)
            {
                var others = g.Owners.Where(o => o != identityId).Select(OwnerLabel).ToList();
                string coOwners = others.Count > 0 ? $" (co-owners: {string.Join(", ", others)})" : "";
                string builtBy = g.OwnersFromBuiltBy ? " (no owners; matched by 'Built by')" : "";
                sb.AppendLine($"  - {GridNames(g, false)}{coOwners}{builtBy}");
            }

            return sb.ToString();
        }

        // ───────────────────────── Предупреждения ─────────────────────────

        public class WarnResult
        {
            public int Notified;
            public List<string> OfflineOwners = new List<string>();
        }

        public static WarnResult SendWarnings(IReadOnlyCollection<string> subtypes, List<CleanupGroup> groups)
        {
            var perOwner = new Dictionary<long, List<string>>();

            // NPC-мусор не рассылаем: у него нет игроков-владельцев.
            foreach (var g in groups.Where(g => !g.Satisfies && !g.IsNpcScrap))
            {
                foreach (var owner in g.Owners)
                {
                    List<string> list;
                    if (!perOwner.TryGetValue(owner, out list))
                    {
                        list = new List<string>();
                        perOwner[owner] = list;
                    }
                    list.AddRange(g.Grids.Select(x => $"{x.DisplayName} ({x.BlocksCount} blocks)"));
                }
            }

            var pending = Pending;
            var online = new HashSet<long>();
            foreach (var p in MySession.Static.Players.GetOnlinePlayers())
                online.Add(p.Identity.IdentityId);

            var result = new WarnResult();
            foreach (var kv in perOwner)
            {
                if (!online.Contains(kv.Key))
                {
                    result.OfflineOwners.Add(OwnerLabel(kv.Key));
                    continue;
                }

                var names = kv.Value;
                const int maxNames = 10;
                string shown = string.Join(", ", names.Take(maxNames));
                if (names.Count > maxNames)
                    shown += $" and {names.Count - maxNames} more";

                string deadline = pending != null ? $" Scheduled deletion: {DescribeDue(pending.DueUtc)}." : "";
                string msg = $"WARNING: your grids will be removed: {shown}.{deadline} " +
                             $"To keep a grid it must have at least one block of: {RequiredBlockLabel}. " +
                             "Type !gm cleanup my for details.";

                MyVisualScriptLogicProvider.SendChatMessage(msg, "GridCleanup", kv.Key, "Red");
                result.Notified++;
            }

            Log?.Info($"GridCleanup: warnings sent to {result.Notified} online player(s), offline owners: {result.OfflineOwners.Count}");
            return result;
        }

        // ───────────────────────── Удаление ─────────────────────────

        public class DeleteResult
        {
            public int GridsDeleted;
            public int GroupsDeleted;
            public int ScrapGridsDeleted;
            public int GroupsSkippedPilot;
        }

        public static DeleteResult DeleteUnsatisfied(List<CleanupGroup> groups)
        {
            var result = new DeleteResult();
            var run = new DeletedRun { RunUtc = DateTime.UtcNow }; 

            foreach (var g in groups.Where(g => !g.Satisfies))
            {
                // Не удаляем группы, в которых прямо сейчас кто-то сидит.
                if (g.HasPilot)
                {
                    result.GroupsSkippedPilot++;
                    Log?.Info($"GridCleanup: skipped (pilot inside): {GridNames(g, true)}");
                    continue;
                }

                string owners = g.Owners.Count > 0 ? string.Join(", ", g.Owners.Select(OwnerLabel)) : "none (NPC)";
                string kind = g.IsNpcScrap ? "NPC scrap" : (g.OwnersFromBuiltBy ? "ownerless, matched by BuiltBy" : "player");
                bool any = false;

                foreach (var grid in g.Grids)
                {
                    if (grid.Closed || grid.MarkedForClose)
                        continue;

                    // Данные для журнала снимаем ДО закрытия грида.
                    string name = grid.DisplayName;
                    long entityId = grid.EntityId;
                    int blocks = grid.BlocksCount;

                    Log?.Info($"GridCleanup: DELETING '{name}' ({entityId}, {blocks} blocks) [{kind}], owners: {owners}");
                    try
                    {
                        grid.Close();
                        result.GridsDeleted++;
                        any = true;

                        if (g.IsNpcScrap)
                        {
                            result.ScrapGridsDeleted++;
                        }
                        else
                        {
                            run.Grids.Add(new DeletedGridRecord
                            {
                                Name = name,
                                EntityId = entityId,
                                Blocks = blocks,
                                OwnerIds = g.Owners.ToList()
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        Log?.Error(e, $"GridCleanup: failed to close grid {name}");
                    }
                }

                if (any)
                    result.GroupsDeleted++;
            }

            // Журнал перезаписываем только если реально удалены гриды игроков:
            // чистка одного NPC-мусора не должна стирать список, который ещё хотят посмотреть игроки.
            if (run.Grids.Count > 0)
            {
                SaveLastRun(run);

                // Кто онлайн прямо сейчас — получает итог сразу; остальным он придёт через минуту после захода.
                NotifyOnlinePlayers(run);
            }

            return result;
        }
    }
}
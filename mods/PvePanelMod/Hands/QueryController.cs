// 基于 hpenguins/HandViewerMod（github.com/hpenguins/HandViewerMod，AGPL-3.0）的 QueryController.cs 改写，原作者 hpenguins。
// 合并进 PVE 面板后的改动见文件内「改动」注释。
using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Configuration;

namespace PvePanelMod.Hands
{
    public sealed class HandCard
    {
        public int cardUid, cardId, purifyNum, battleCost;
        public bool isTemp;
        public string name, description;
    }
    public sealed class PlayerHand
    {
        public string playerId, nick, handReason;
        public bool handKnown;
        public int? handCount;
        public List<HandCard> cards = new List<HandCard>();
    }
    public sealed class HandSnapshot
    {
        public string roomId, sampledUtc;
        public bool officialSpectating;
        public List<PlayerHand> players = new List<PlayerHand>();

        public static HandSnapshot Parse(string json, string room)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 1024 * 1024) throw new Exception("响应为空或过大");
            ValidateEnvelope(json);
            object raw;
            if (!CesiumJson.TryDeserialize(json, out raw) || !(raw is Dictionary<string, object> root))
                throw new Exception("响应不是有效 JSON 对象");
            if (!root.TryGetValue("officialSpectating", out var authority) || !(authority is bool b) || !b)
                throw new Exception("服务没有官方观战数据");
            if (!root.TryGetValue("roomId", out var id) || !(id is string s) || s != room)
                throw new Exception("响应房间不匹配");
            if (!root.TryGetValue("players", out var roster) || !(roster is System.Collections.IList list) || list.Count == 0 || list.Count > 4)
                throw new Exception("玩家列表无效");
            var result = (HandSnapshot)CesiumJson.ToObject(raw, typeof(HandSnapshot));
            if (result == null || result.players == null || result.players.Count != list.Count) throw new Exception("响应解析失败");
            var ids = new HashSet<string>();
            for (int i = 0; i < result.players.Count; i++)
            {
                var player = result.players[i];
                var fields = list[i] as Dictionary<string, object>;
                long playerId;
                if (fields == null || !fields.TryGetValue("handKnown", out var known) || !(known is bool)
                    || !fields.TryGetValue("playerId", out var rawId) || !(rawId is string)
                    || player == null || !long.TryParse(player.playerId, out playerId) || playerId <= 0 || !ids.Add(player.playerId))
                    throw new Exception("玩家字段无效");
                if (!player.handKnown) { player.cards = new List<HandCard>(); continue; }
                if (!fields.TryGetValue("cards", out var cards) || !(cards is System.Collections.IList)
                    || player.cards == null || player.cards.Count > 128 || player.handCount != player.cards.Count)
                    throw new Exception("手牌列表不完整");
                var cardList = (System.Collections.IList)cards;
                if (!Integer(fields, "handCount", 0, 128)) throw new Exception("手牌数量无效");
                var uids = new HashSet<int>();
                for (int n = 0; n < player.cards.Count; n++)
                {
                    var card = player.cards[n];
                    var values = cardList[n] as Dictionary<string, object>;
                    if (values == null || !Integer(values, "cardId", 1, int.MaxValue)
                        || !Integer(values, "cardUid", 1, int.MaxValue) || !Integer(values, "purifyNum", 0, int.MaxValue)
                        || !Integer(values, "battleCost", -1, int.MaxValue) || !values.TryGetValue("isTemp", out var temporary) || !(temporary is bool)
                        || card == null || !uids.Add(card.cardUid)) throw new Exception("手牌字段无效");
                }
            }
            return result;
        }
        static void ValidateEnvelope(string json)
        {
            int depth = 0; bool quoted = false, escaped = false, ended = false;
            foreach (char c in json)
            {
                if (ended) { if (!char.IsWhiteSpace(c)) throw new Exception("响应含多余内容"); continue; }
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    else if (c < 32) throw new Exception("字符串控制字符无效");
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{' || c == '[') { if (++depth > 16) throw new Exception("响应嵌套过深"); }
                else if (c == '}' || c == ']') { if (--depth < 0) throw new Exception("响应结构无效"); if (depth == 0) ended = true; }
            }
            if (quoted || depth != 0 || !ended) throw new Exception("响应未闭合");
        }
        static bool Integer(Dictionary<string, object> fields, string key, int min, int max)
        {
            if (!fields.TryGetValue(key, out var value) || !(value is double d)) return false;
            return d >= min && d <= max && d == Math.Floor(d);
        }
    }

    public interface IHandRequest : IDisposable
    {
        bool Done { get; }
        long Code { get; }
        string Text { get; }
        string RetryAfter { get; }
    }
    public sealed class HandQueryController : IDisposable
    {
        // 改动（限流，原版「连接失败」实为 HTTP 429）：
        //   1. 两次成功查询至少隔 MinSampleIntervalMs（原版 1.5 秒）；
        //   2. 手牌变动后攒 DirtyDelayMs 再查（原版 0.6 秒），连着出牌只查一次；
        //   3. 重新展开不再清掉服务器要求的等待（原版 retryAt = 0，会无视 Retry-After 立刻重查）。
        public const long MinSampleIntervalMs = 5000, DirtyDelayMs = 1500;
        /// <summary>Only reported while no hand strip is expanded; an expanded strip never shows this.</summary>
        public const string CollapsedStatus = "手牌已收起，暂停查询";
        /// <summary>配置里把第三方手牌服务关掉时的状态（不然会一直报「等待对局观战码」）。</summary>
        public const string ServiceOffStatus = "手牌服务已关闭";
        const string WaitingRoomStatus = "等待对局观战码";
        const string WaitingRefreshStatus = "等待查询";
        readonly Func<string, IHandRequest> start;
        IHandRequest request;
        string room, code;
        bool dirty, viewing, serviceEnabled = true;
        /// <summary>非空 = 配置层面就不能查询（比如服务地址写错），状态直接显示这句，直到下次配置。</summary>
        string blocked;
        long due, retryAt;
        int failures;
        public HandSnapshot Snapshot { get; private set; }
        public string Status { get; private set; } = WaitingRoomStatus;
        public int Revision { get; private set; }
        public bool Running => request != null;
        public bool IsStale { get; private set; }
        public HandQueryController(Func<string, IHandRequest> start) { this.start = start; }

        /// <summary>能不能查：服务开着、配置也没有问题。</summary>
        bool Active => serviceEnabled && blocked == null;
        /// <summary>不能查时该显示的话（服务关着 / 配置有问题）。</summary>
        string IdleStatus => !serviceEnabled ? ServiceOffStatus : blocked;

        /// <summary>
        /// 配置层面的问题（比如第三方服务地址非法）：不查询、状态直接显示原因。
        /// 传 null 表示恢复正常；地址改好后下一次 Configure 会自动清掉。
        /// </summary>
        public void SetBlocked(string reason, long now)
        {
            reason = string.IsNullOrEmpty(reason) ? null : reason;
            if (blocked == reason) return;
            blocked = reason;
            DisposeRequest();
            dirty = false;
            if (!serviceEnabled) return;
            if (blocked != null) { Status = blocked; Revision++; return; }
            Status = viewing
                ? (string.IsNullOrEmpty(room) || string.IsNullOrEmpty(code) ? WaitingRoomStatus : WaitingRefreshStatus)
                : CollapsedStatus;
            Revision++;
            if (viewing) Refresh(now);
        }

        /// <summary>
        /// 配置里开关第三方手牌服务。关掉后一律不查询、不再对外报「等待对局观战码」（那是服务开着、只是还没拿到观战码时说的话）。
        /// 调用方本来就只在开关打开时才 SetViewing(true)，这里再兜一层，免得以后有人绕过。
        /// </summary>
        public void SetServiceEnabled(bool enabled, long now)
        {
            if (!enabled)
            {
                DisposeRequest();
                dirty = false;
                if (serviceEnabled) { serviceEnabled = false; Revision++; }
                if (Status != ServiceOffStatus) { Status = ServiceOffStatus; Revision++; }
                return;
            }
            if (serviceEnabled) return;
            serviceEnabled = true;
            if (blocked != null)
            {
                if (Status != blocked) { Status = blocked; Revision++; }
                return;
            }
            Status = viewing ? (string.IsNullOrEmpty(room) || string.IsNullOrEmpty(code) ? WaitingRoomStatus : WaitingRefreshStatus) : CollapsedStatus;
            Revision++;
            if (viewing) Refresh(now);
        }

        public void SetRoom(string id, string watchCode, long now)
        {
            if (room == id && code == watchCode) return;
            DisposeRequest();
            room = id; code = watchCode; Snapshot = null; IsStale = false; failures = 0; retryAt = 0;
            dirty = Active && viewing && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(watchCode);
            due = now;
            Status = !Active ? IdleStatus : !viewing ? CollapsedStatus : dirty ? WaitingRefreshStatus : WaitingRoomStatus;
            Revision++;
        }
        public void SetViewing(bool visible, long now)
        {
            if (viewing == visible) return;
            viewing = visible;
            if (visible)
            {
                // 改动 3：重新展开是一次手动刷新，但仍然遵守上一次的等待（成功后的最短间隔或服务器的 Retry-After）
                Status = !Active ? IdleStatus
                       : string.IsNullOrEmpty(room) || string.IsNullOrEmpty(code) ? WaitingRoomStatus : WaitingRefreshStatus;
                Revision++;
                Refresh(now);
            }
            else
            {
                DisposeRequest(); dirty = false;
                IsStale = Snapshot != null;
                Status = !Active ? IdleStatus : CollapsedStatus; Revision++;
            }
        }
        public void MarkDirty(long now)
        {
            if (!Active || !viewing) return;
            if (string.IsNullOrEmpty(room) || string.IsNullOrEmpty(code))
            {
                if (Status != WaitingRoomStatus) { Status = WaitingRoomStatus; Revision++; }
                return;
            }
            if (!dirty) due = now + DirtyDelayMs;   // 改动 2
            dirty = true;
            if (Snapshot != null && !IsStale) { IsStale = true; Revision++; }
        }
        public void Refresh(long now) { MarkDirty(now); due = now; }
        public void Tick(long now)
        {
            if (!Active || !viewing) return;
            if (request != null)
            {
                bool completed = false;
                try
                {
                    if (!request.Done) return;
                    completed = true;
                    var status = request.Code;
                    if (status != 200)
                    {
                        int seconds;
                        long delay = (status == 429 || status == 503) && int.TryParse(request.RetryAfter, out seconds)
                            ? Math.Max(1, Math.Min(300, seconds)) * 1000L : Backoff();
                        var message = ServerMessage(request.Text);
                        Fail(now, delay, "查询失败 HTTP " + status + (message == null ? "" : "：" + message));
                    }
                    else
                    {
                        var text = request.Text;
                        try
                        {
                            Snapshot = HandSnapshot.Parse(text, room);
                            IsStale = dirty; failures = 0; retryAt = now + MinSampleIntervalMs; Status = "已采样";   // 改动 1
                            Revision++;
                        }
                        catch
                        {
                            var message = ServerMessage(text);
                            Fail(now, Backoff(), message == null ? "查询失败：网络或响应数据不可用" : "查询失败：" + message);
                        }
                    }
                }
                catch { completed = true; Fail(now, Backoff(), "查询失败：网络或响应数据不可用"); }
                finally { if (completed) DisposeRequest(); }
            }
            if (!dirty || request != null || now < due || now < retryAt) return;
            dirty = false;
            try { request = start(code); if (request == null) throw new Exception(); Status = "查询中…"; Revision++; }
            catch { Fail(now, Backoff(), "无法启动 HTTP 请求"); }
        }
        static string ServerMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 * 1024) return null;
            text = text.Trim();
            string message = null;
            if (text.StartsWith("{") || text.StartsWith("[") || text.StartsWith("\""))
            {
                try
                {
                    object raw;
                    if (CesiumJson.TryDeserialize(text, out raw)) message = MessageField(raw, 0);
                }
                catch { }
            }
            else if (!text.StartsWith("<")) message = text;
            if (string.IsNullOrWhiteSpace(message)) return null;
            // Bound tooltip length and remove control characters; display as plain text.
            var clean = new System.Text.StringBuilder();
            foreach (char c in message)
            {
                if (clean.Length >= 512) { clean.Append("…"); break; }
                clean.Append(char.IsControl(c) ? ' ' : c);
            }
            var result = clean.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        static string MessageField(object value, int depth)
        {
            if (value is string text) return string.IsNullOrWhiteSpace(text) ? null : text;
            if (depth >= 3 || !(value is Dictionary<string, object> fields)) return null;
            foreach (var key in new[] { "message", "detail", "error" })
                if (fields.TryGetValue(key, out var child))
                {
                    var message = MessageField(child, depth + 1);
                    if (message != null) return message;
                }
            return null;
        }
        long Backoff() { return Math.Min(60000, 2000L << Math.Min(5, failures)); }
        void Fail(long now, long delay, string message)
        {
            failures++; dirty = true; IsStale = Snapshot != null; retryAt = now + delay;
            Status = message + (Snapshot == null ? "（稍后重试）" : "（显示旧快照，稍后重试）"); Revision++;
        }
        void DisposeRequest() { try { request?.Dispose(); } catch { } request = null; }
        public void Dispose() { DisposeRequest(); Snapshot = null; room = code = null; dirty = false; }
    }
}

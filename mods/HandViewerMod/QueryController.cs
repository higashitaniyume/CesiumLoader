using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Configuration;

namespace HandViewerMod
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
        readonly Func<string, IHandRequest> start;
        IHandRequest request;
        string room, code;
        bool dirty;
        long due, retryAt;
        int failures;
        public HandSnapshot Snapshot { get; private set; }
        public string Status { get; private set; } = "等待对局观战码";
        public int Revision { get; private set; }
        public bool Running => request != null;
        public bool IsStale { get; private set; }
        public HandQueryController(Func<string, IHandRequest> start) { this.start = start; }

        public void SetRoom(string id, string watchCode, long now)
        {
            if (room == id && code == watchCode) return;
            DisposeRequest();
            room = id; code = watchCode; Snapshot = null; IsStale = false; failures = 0; retryAt = 0;
            dirty = !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(watchCode);
            due = now; Status = dirty ? "等待查询" : "等待对局观战码"; Revision++;
        }
        public void MarkDirty(long now)
        {
            if (string.IsNullOrEmpty(room) || string.IsNullOrEmpty(code)) return;
            if (!dirty) due = now + 600;
            dirty = true;
            if (Snapshot != null && !IsStale) { IsStale = true; Revision++; }
        }
        public void Refresh(long now) { MarkDirty(now); due = now; }
        public void Tick(long now)
        {
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
                        Fail(now, delay, "查询失败 HTTP " + status);
                    }
                    else
                    {
                        Snapshot = HandSnapshot.Parse(request.Text, room);
                        IsStale = dirty; failures = 0; retryAt = now + 1500; Status = "已采样"; Revision++;
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

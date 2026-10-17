using System;
using System.Collections.Generic;
using Core.Net;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using Cysharp.Threading.Tasks;
using GameLogic;
using Google.Protobuf.Collections;
using party.protocol;
using Tools;

namespace CombatOddsMod
{
    /// <summary>
    /// 主动技能冷却表的数据源: 自己按 S2C 消息维护每个玩家的剩余冷却。
    ///
    /// 游戏只为本地玩家实时维护 <c>Property.activeSkillCD</c>, 而且客户端会把同一帧
    /// <c>RoundStartS2C.SkillCds</c> 套给所有玩家, 非归属者按自己的技能 ID 查不到键就被刷成 0,
    /// 所以队友(以及每帧顺序靠前的自己)读实时字段必然显示错误。这里改为取原始消息:
    ///   1. 回合开始帧(每个玩家各一帧, 帧内只带该玩家的主动技能键);
    ///   2. 释放技能(SDK 的 SkillUsed 事件之后, 读游戏刚写进释放者字段的值, 即本次生效上限);
    ///   3. 冷却修正(UpdateHeroAttrS2C 里的 Cd, 广播, 含目标玩家)。
    ///
    /// 只包装 RoundStartS2C 一条回调: UseEffectCardS2C 由 SDK 自己包装并每秒重挂,
    /// 再在上面套一层会让两条包装互相把对方装进链里, 一条消息被处理几十次直至栈溢出。
    /// 包装器一律在创建时**捕获**原始委托, 不能在调用时读可变的静态字段(那会指向更新后的包装, 形成自我递归)。
    ///
    /// 消息在网络线程到达, 先入队; 覆盖层每次刷新时在主线程取队列交给 <see cref="SkillCooldownSession"/>,
    /// 避免离开主线程读游戏对象。只读取游戏状态, 不回写。
    /// </summary>
    internal sealed class SkillCooldownTracker : IDisposable
    {
        private sealed class Snapshot
        {
            internal long OwnerPlayerId;
            internal int Round;
            internal KeyValuePair<int, int>[] Cooldowns;
        }

        private readonly SkillCooldownSession _session = new SkillCooldownSession();
        private readonly object _gate = new object();
        private readonly List<Snapshot> _pending = new List<Snapshot>();
        private readonly List<KeyValuePair<long, int>> _pendingCasts = new List<KeyValuePair<long, int>>();
        private readonly List<string> _pendingLogs = new List<string>();
        private readonly List<KeyValuePair<long, int>> _pendingServerCd = new List<KeyValuePair<long, int>>();
        private readonly Dictionary<long, float> _lastQueryAt = new Dictionary<long, float>();

        /// <summary>把每条数据源与最终显示值写进日志(排查显示口径用)。</summary>
        internal bool LogDetail;

        /// <summary>
        /// 是否用 <c>GetHeroInfoC2S</c> 主动查询每名玩家的当前冷却(服务器权威值)。
        /// 队友的每轮 -1 只存在于发给本人的 1015 里, 不查询的话只能本地推算。
        /// </summary>
        internal bool UseServerCd = true;

        // 挂钩是进程级的: 同一时刻只跟随最后启动的这个追踪器。
        private static SkillCooldownTracker _active;
        private static RoundStartS2CRPC.OnRoundStartS2CServerDelegate _ourRoundStart;
        private static RoundStartS2CRPC.OnRoundStartS2CServerDelegate _roundStartOver;
        // 每局最多安装几次: 游戏重设回调需要重装, 但别的组件在上面反复包装时不能无限加层。
        private const int MaxRoundStartInstalls = 4;
        private static int _roundStartInstalls;

        private static GetHeroInfoS2CRPC.OnGetHeroInfoS2CServerDelegate _ourHeroInfo;
        private static GetHeroInfoS2CRPC.OnGetHeroInfoS2CServerDelegate _heroInfoOver;
        private const int MaxHeroInfoInstalls = 4;
        private static int _heroInfoInstalls;

        private BattleLogic _battle;
        private long _lastTurnPlayer;
        private bool _warned;
        private bool _disposed;
        private bool _hookLogged;
        private bool _ownerLogged;

        internal void Start()
        {
            _active = this;
            try
            {
                GameEvents.HeroAttrUpdated += OnHeroAttrUpdated;
                GameEvents.SkillUsed += OnSkillUsed;
                GameEvents.StartAutoHook();
            }
            catch (Exception e)
            {
                WarnOnce("技能 CD 事件订阅失败: " + e.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                GameEvents.HeroAttrUpdated -= OnHeroAttrUpdated;
                GameEvents.SkillUsed -= OnSkillUsed;
            }
            catch { }
            if (ReferenceEquals(_active, this)) _active = null;
            UnhookRoundStart();
            UnhookHeroInfo();
            lock (_gate)
            {
                _session.Reset();
                _pending.Clear();
                _pendingCasts.Clear();
                _pendingServerCd.Clear();
                _lastQueryAt.Clear();
            }
            _battle = null;
        }

        /// <summary>
        /// 已维护的剩余冷却; 没有数据时返回 false, 调用方不显示。
        /// 优先按玩家 ID 取, 取不到再按该玩家自己的主动技能键兜底。
        /// </summary>
        internal bool TryGetRemaining(long playerId, int skillId, out int remaining)
        {
            lock (_gate) return _session.TryGetRemaining(playerId, skillId, out remaining);
        }

        /// <summary>该玩家最近一次释放技能时的生效上限; 还没释放过时返回 false。</summary>
        internal bool TryGetCap(long playerId, int skillId, out int cap)
        {
            lock (_gate) return _session.TryGetCap(playerId, skillId, out cap);
        }

        /// <summary>随覆盖层刷新调用: 换局时清表, 维护挂钩, 并把队列里的精确值写入表中。</summary>
        internal void Poll()
        {
            SyncBattle();
            EnsureRoundStartHook();
            EnsureHeroInfoHook();
            DrainPending();
            ResolveCasts();
            TrackTurns();
            SeedMissing();
            CollectSharedSkills();
            QueryServerCd();
            DrainServerCd();
            FlushLogs();
        }

        /// <summary>
        /// 主线程: 观察"轮到谁了"。服务器在每名玩家回合开始时把他的剩余冷却 -1,
        /// 而 1015 只发给本人, 所以队友只能靠这个信号递减。
        /// 本地玩家自己有精确帧, 不在这里扣, 避免扣两次。
        /// </summary>
        private void TrackTurns()
        {
            var battle = _battle;
            if (battle?.PlayerDatas == null || battle.PlayerDatas.Count == 0) return;
            long current;
            try { current = battle.curPlayerId; } catch { return; }
            if (current == _lastTurnPlayer) return;
            _lastTurnPlayer = current;
            if (current == 0) return;
            bool self = false;
            try { self = Players.IsSelf(current); } catch { }
            if (self) return;
            lock (_gate) _session.OnTurnStart(current);
            if (LogDetail)
            {
                int value;
                lock (_gate) _session.TryGetRemaining(current, 0, out value);
                Log("CD回合 player=" + current + " → " + value);
            }
        }

        internal void Log(string message)
        {
            if (!LogDetail || string.IsNullOrEmpty(message)) return;
            lock (_gate) _pendingLogs.Add(message);
        }

        /// <summary>主线程: 把网络线程攒下的排查日志一次性写出去。</summary>
        private void FlushLogs()
        {
            string[] logs;
            lock (_gate)
            {
                if (_pendingLogs.Count == 0) return;
                logs = _pendingLogs.ToArray();
                _pendingLogs.Clear();
            }
            for (int i = 0; i < logs.Length; i++) SdkLog.Info("CombatOdds", logs[i]);
        }

        private void SyncBattle()
        {
            BattleLogic battle = null;
            try { battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle; } catch { }
            if (ReferenceEquals(battle, _battle)) return;
            _battle = battle;
            _lastTurnPlayer = 0;
            _lastRound = 0;
            _queryPending = true;
            // 换局: 游戏会重设回调, 安装预算重新计数(包装身份保留, 避免把自己的包装再包一层)。
            _roundStartInstalls = 0;
            _heroInfoInstalls = 0;
            lock (_gate)
            {
                _session.Reset();
                _pending.Clear();
                _pendingCasts.Clear();
                _pendingServerCd.Clear();
                _lastQueryAt.Clear();
            }
        }

        // ---------- 挂钩 ----------

        /// <summary>
        /// 只在战斗数据就绪时装一次。已经装过就不再动: 若别的组件把我们的包装又包了一层,
        /// 重新安装会让链越叠越长; 只有字段回到最初的游戏委托(说明被重置)时才重装。
        /// </summary>
        private static void EnsureRoundStartHook()
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed) return;
            try
            {
                var battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle;
                if (battle?.PlayerDatas == null || battle.PlayerDatas.Count == 0) return;
                var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.RoundStartS2C;
                if (rpc == null) return;
                var handler = rpc.OnRoundStartS2CServerCallBackAsync;
                if (handler == null) return;
                // 已装好 / 本局安装次数用尽时不再安装, 否则回调链会不断变长。
                var budget = MaxRoundStartInstalls - _roundStartInstalls;
                var wrapped = HookChain.Build(handler, _ourRoundStart, budget, original =>
                {
                    // 捕获 original: 调用时不能读可变的 _roundStartOver, 那会指向更新后的包装并自我递归。
                    RoundStartS2CRPC.OnRoundStartS2CServerDelegate hook = (model, errId, isDispatch) =>
                    {
                        if (errId == 0) Enqueue(model?.PlayerId ?? 0, model?.Round ?? 0, model?.SkillCds);
                        return original(model, errId, isDispatch);
                    };
                    return hook;
                });
                if (wrapped == null) return;
                _roundStartInstalls++;
                _roundStartOver = handler;
                _ourRoundStart = wrapped;
                rpc.OnRoundStartS2CServerCallBackAsync = wrapped;
                if (!tracker._hookLogged)
                {
                    tracker._hookLogged = true;
                    SdkLog.Write("CombatOdds", "技能 CD 已挂钩: RoundStartS2C");
                }
            }
            catch (Exception e)
            {
                if (tracker != null && !tracker._warned)
                {
                    tracker._warned = true;
                    SdkLog.Warn("CombatOdds", "技能 CD 消息挂钩失败: " + e.Message);
                }
            }
        }

        private static void UnhookRoundStart()
        {
            try
            {
                var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.RoundStartS2C;
                if (rpc != null && ReferenceEquals(rpc.OnRoundStartS2CServerCallBackAsync, _ourRoundStart))
                    rpc.OnRoundStartS2CServerCallBackAsync = _roundStartOver;
            }
            catch { }
            _ourRoundStart = null;
            _roundStartOver = null;
        }

        /// <summary>
        /// 包装 <c>GetHeroInfoS2C</c> 的回包(基础游戏用它显示"玩家信息面板"里的技能 CD)。
        /// 这条 RPC 不在 SDK 的包装清单里, 按 HookChain 的"只装一次 + 捕获原始委托"策略装。
        /// </summary>
        private static void EnsureHeroInfoHook()
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed || !tracker.UseServerCd) return;
            try
            {
                var battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle;
                if (battle?.PlayerDatas == null || battle.PlayerDatas.Count == 0) return;
                var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.GetHeroInfoS2C;
                if (rpc == null) return;
                var handler = rpc.OnGetHeroInfoS2CServerCallBackAsync;
                if (handler == null) return;
                var budget = MaxHeroInfoInstalls - _heroInfoInstalls;
                var wrapped = HookChain.Build(handler, _ourHeroInfo, budget, original =>
                {
                    GetHeroInfoS2CRPC.OnGetHeroInfoS2CServerDelegate hook = (model, errId, isDispatch) =>
                    {
                        if (errId == 0 && model != null) EnqueueServerCd(model.PlayerId, model.Cd);
                        return original(model, errId, isDispatch);
                    };
                    return hook;
                });
                if (wrapped == null) return;
                _heroInfoInstalls++;
                _heroInfoOver = handler;
                _ourHeroInfo = wrapped;
                rpc.OnGetHeroInfoS2CServerCallBackAsync = wrapped;
            }
            catch (Exception e)
            {
                if (tracker != null && !tracker._warned)
                {
                    tracker._warned = true;
                    SdkLog.Warn("CombatOdds", "玩家信息(CD 查询)挂钩失败: " + e.Message);
                }
            }
        }

        private static void UnhookHeroInfo()
        {
            try
            {
                var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.GetHeroInfoS2C;
                if (rpc != null && ReferenceEquals(rpc.OnGetHeroInfoS2CServerCallBackAsync, _ourHeroInfo))
                    rpc.OnGetHeroInfoS2CServerCallBackAsync = _heroInfoOver;
            }
            catch { }
            _ourHeroInfo = null;
            _heroInfoOver = null;
        }

        /// <summary>网络线程: 记下查询回包里的服务器 CD 绝对值。</summary>
        private static void EnqueueServerCd(long playerId, int cd)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed || playerId == 0) return;
            lock (tracker._gate) tracker._pendingServerCd.Add(new KeyValuePair<long, int>(playerId, cd));
        }

        /// <summary>每名玩家最多多久查一次(秒)。查询只是读玩家信息, 频率压到很低。</summary>
        private const float QueryInterval = 5f;
        private int _lastRound;
        private bool _queryPending;

        /// <summary>
        /// 主线程: 向服务器查询队友的当前冷却(<c>GetHeroInfoC2S</c>, 与打开玩家信息面板同一条请求)。
        /// 只在"新的一轮"(自己的 1015 到达)或开局时查一次, 平时用本地递减做平滑。
        /// 本人有每轮的精确帧, 不查。
        /// </summary>
        private void QueryServerCd()
        {
            if (!UseServerCd || !_queryPending) return;
            var battle = _battle;
            if (battle?.PlayerDatas == null) return;
            var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.GetHeroInfoC2S;
            if (rpc == null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = 0; i < battle.PlayerDatas.Count; i++)
            {
                var data = battle.PlayerDatas[i];
                var player = data?.player;
                if (player == null) continue;
                try { if (data.characterType != CharacterType.Hero) continue; } catch { continue; }
                long playerId = player.Id;
                if (playerId == 0) continue;
                try { if (Players.IsSelf(playerId)) continue; } catch { }
                float last;
                if (_lastQueryAt.TryGetValue(playerId, out last) && now - last < QueryInterval) continue;
                _lastQueryAt[playerId] = now;
                try { rpc.GetHeroInfoC2SCall(new GetHeroInfoC2S { PlayerId = playerId }); } catch { }
                Log("CD查询 player=" + playerId);
            }
            // 这一轮处理完: 等下一轮的帧再查, 避免每 0.1 s 重复发。
            _queryPending = false;
        }

        /// <summary>主线程: 把查询回包的服务器绝对值写进表(权威值覆盖本地推算)。</summary>
        private void DrainServerCd()
        {
            KeyValuePair<long, int>[] items;
            lock (_gate)
            {
                if (_pendingServerCd.Count == 0) return;
                items = _pendingServerCd.ToArray();
                _pendingServerCd.Clear();
            }
            for (int i = 0; i < items.Length; i++)
            {
                long playerId = items[i].Key;
                int cd = items[i].Value;
                if (cd < 0 || !SkillCooldownRules.IsPlausible(cd)) continue;
                lock (_gate) _session.ApplyCorrection(playerId, cd);
                Log("CD查询回包 player=" + playerId + " cd=" + cd);
            }
        }

        // ---------- 数据源 ----------

        /// <summary>网络线程: 只拷贝消息内容, 不读游戏对象。</summary>
        private static void Enqueue(long ownerPlayerId, int round, MapField<int, int> cooldowns)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed || cooldowns == null) return;
            var copy = new KeyValuePair<int, int>[cooldowns.Count];
            int index = 0;
            foreach (var pair in cooldowns) copy[index++] = pair;
            lock (tracker._gate)
            {
                tracker._pending.Add(new Snapshot { OwnerPlayerId = ownerPlayerId, Round = round, Cooldowns = copy });
            }
        }

        /// <summary>网络线程: 冷却修正(遗物/天赋/地图效果)对所有玩家都广播, 值本身就是剩余冷却。</summary>
        private static void OnHeroAttrUpdated(UpdateHeroAttrS2C model)
        {
            if (model?.EffectDatas == null) return;
            var tracker = _active;
            if (tracker == null || tracker._disposed) return;
            for (int i = 0; i < model.EffectDatas.Count; i++)
            {
                var cd = model.EffectDatas[i]?.Cd;
                if (cd == null) continue;
                lock (tracker._gate) tracker._session.ApplyCorrection(cd.PlayerId, cd.Cd);
                tracker.Log("CD修正 player=" + cd.PlayerId + " cd=" + cd.Cd);
            }
        }

        /// <summary>网络线程: 记下释放者与其技能, 主线程刷新时再去读游戏刚写入的冷却值。</summary>
        private static void OnSkillUsed(long playerId, int skillId)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed) return;
            var key = new KeyValuePair<long, int>(playerId, skillId);
            lock (tracker._gate)
            {
                if (!tracker._pendingCasts.Contains(key)) tracker._pendingCasts.Add(key);
            }
        }

        /// <summary>主线程: 把队列里的服务器绝对值写入会话。</summary>
        private void DrainPending()
        {
            Snapshot[] pending;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                pending = _pending.ToArray();
                _pending.Clear();
            }
            for (int i = 0; i < pending.Length; i++)
            {
                var item = pending[i];
                int ownerSkillId = ResolveActiveSkillId(item.OwnerPlayerId);
                if (item.Round > 0 && item.Round != _lastRound)
                {
                    _lastRound = item.Round;
                    _queryPending = true;   // 新的一轮: 向服务器校准一次队友 CD
                }
                lock (_gate) _session.ApplyFrame(item.OwnerPlayerId, ownerSkillId, item.Cooldowns, item.Round);
                LogUnknownOwner(item);
                if (LogDetail)
                {
                    Log("CD帧 owner=" + item.OwnerPlayerId + " round=" + item.Round + " skill=" + ownerSkillId +
                        " cds=" + FormatCooldowns(item.Cooldowns));
                }
            }
        }

        /// <summary>
        /// 主线程: 释放技能后读释放者的实时字段。游戏在技能演出前就把这一帧的 SkillCds 写进去,
        /// 所以这就是本次生效的冷却上限; 读不到(被别的帧刷成 0)就等该玩家自己的下一帧。
        /// </summary>
        private void ResolveCasts()
        {
            KeyValuePair<long, int>[] casts;
            lock (_gate)
            {
                if (_pendingCasts.Count == 0) return;
                casts = _pendingCasts.ToArray();
                _pendingCasts.Clear();
            }
            for (int i = 0; i < casts.Length; i++)
            {
                long playerId = casts[i].Key;
                int skillId = casts[i].Value;
                var data = GetPlayerData(playerId);
                var property = data?.Property;
                if (property == null) continue;
                // 怪物也会释放技能(107101/107201/107301 等), 它们不占头像位, 不进冷却表。
                try { if (data.characterType != CharacterType.Hero) continue; } catch { continue; }
                int applied;
                try { applied = property.activeSkillCD.Value; } catch { continue; }
                if (LogDetail)
                    Log("CD释放 player=" + playerId + " skill=" + skillId + " field=" + applied);
                if (applied <= 0 || !SkillCooldownRules.IsPlausible(applied)) continue;
                lock (_gate) _session.ApplyCast(playerId, skillId, applied);
            }
        }

        /// <summary>帧归属者不在本局玩家列表里时记一次, 便于排查 ID 口径不一致。</summary>
        private void LogUnknownOwner(Snapshot item)
        {
            if (_ownerLogged) return;
            if (GetPlayerData(item.OwnerPlayerId) != null) return;
            _ownerLogged = true;
            int key = item.Cooldowns.Length == 1 ? item.Cooldowns[0].Key : 0;
            SdkLog.Write("CombatOdds", "技能 CD 帧归属者未匹配到玩家: owner=" + item.OwnerPlayerId + " skill=" + key);
        }

        // ---------- 辅助 ----------

        /// <summary>
        /// 主线程: 用每个角色自带的 Hero.SkillCds(房间快照, 按玩家独立)补一次初值。
        /// 空表 = 当时就绪(0), 也要补 —— 否则该玩家在释放技能前根本没有条目, 覆盖层会一直不显示他,
        /// 表现就是"4 个人只看到 3 个人的 CD"。
        /// </summary>
        private void SeedMissing()
        {
            var battle = _battle;
            if (battle?.PlayerDatas == null) return;
            for (int i = 0; i < battle.PlayerDatas.Count; i++)
            {
                var player = battle.PlayerDatas[i]?.player;
                if (player == null) continue;
                // 只补玩家: 怪物也在这张表里, 但它们不占左侧头像位, 补了只会白占表项。
                try { if (player.characterType != CharacterType.Hero) continue; } catch { }
                long playerId = player.Id;
                var table = _session;
                bool known;
                lock (_gate) known = table.TryGetRemaining(playerId, 0, out _);
                if (known) continue;
                int skillId;
                try { skillId = player.GetBattleActiveSkillId(); } catch { continue; }
                if (skillId == 0) continue;
                MapField<int, int> cds;
                try { cds = player.Hero?.SkillCds; } catch { cds = null; }
                if (cds == null) cds = new MapField<int, int>();   // 空表 = 就绪(0), 也要建条目
                lock (_gate) _session.ApplyFrame(playerId, skillId, cds);
                if (LogDetail)
                    Log("CD补齐 seed player=" + playerId + " skill=" + skillId + " heroCds=" + FormatCooldowns(cds));
            }
        }

        private static string FormatCooldowns(IEnumerable<KeyValuePair<int, int>> cds)
        {
            if (cds == null) return "{}";
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var pair in cds)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(pair.Key).Append(':').Append(pair.Value);
            }
            return sb.Append('}').ToString();
        }

        /// <summary>多名玩家共用同一主动技能 ID 时, 不再用技能键兜底, 避免串号。</summary>
        private void CollectSharedSkills()
        {
            var battle = _battle;
            if (battle?.PlayerDatas == null) return;
            var seen = new HashSet<int>();
            var shared = new List<int>();
            var skills = new List<KeyValuePair<long, int>>();
            for (int i = 0; i < battle.PlayerDatas.Count; i++)
            {
                var player = battle.PlayerDatas[i]?.player;
                if (player == null) continue;
                int skillId;
                try { skillId = player.GetBattleActiveSkillId(); } catch { continue; }
                if (skillId == 0) continue;
                skills.Add(new KeyValuePair<long, int>(player.Id, skillId));
                if (!seen.Add(skillId)) shared.Add(skillId);
            }
            lock (_gate)
            {
                for (int i = 0; i < skills.Count; i++) _session.SetPlayerSkill(skills[i].Key, skills[i].Value);
                _session.SetSharedSkills(shared);
            }
        }

        private static int ResolveActiveSkillId(long playerId)
        {
            var data = GetPlayerData(playerId);
            var player = data?.player;
            if (player == null) return 0;
            try { return player.GetBattleActiveSkillId(); } catch { return 0; }
        }

        private static BattlePlayerData GetPlayerData(long playerId)
        {
            try
            {
                var battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle;
                return battle?.GetPlayerDataById(playerId);
            }
            catch { return null; }
        }

        private void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn("CombatOdds", message);
        }
    }
}

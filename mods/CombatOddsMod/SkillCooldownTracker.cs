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
            internal KeyValuePair<int, int>[] Cooldowns;
        }

        private readonly SkillCooldownSession _session = new SkillCooldownSession();
        private readonly object _gate = new object();
        private readonly List<Snapshot> _pending = new List<Snapshot>();
        private readonly List<long> _pendingCasts = new List<long>();

        // 挂钩是进程级的: 同一时刻只跟随最后启动的这个追踪器。
        private static SkillCooldownTracker _active;
        private static RoundStartS2CRPC.OnRoundStartS2CServerDelegate _ourRoundStart;
        private static RoundStartS2CRPC.OnRoundStartS2CServerDelegate _roundStartOver;
        // 每局最多安装几次: 游戏重设回调需要重装, 但别的组件在上面反复包装时不能无限加层。
        private const int MaxRoundStartInstalls = 4;
        private static int _roundStartInstalls;

        private BattleLogic _battle;
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
            lock (_gate)
            {
                _session.Reset();
                _pending.Clear();
                _pendingCasts.Clear();
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
            DrainPending();
            ResolveCasts();
            SeedMissing();
            CollectSharedSkills();
        }

        private void SyncBattle()
        {
            BattleLogic battle = null;
            try { battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle; } catch { }
            if (ReferenceEquals(battle, _battle)) return;
            _battle = battle;
            // 换局: 游戏会重设回调, 安装预算重新计数(包装身份保留, 避免把自己的包装再包一层)。
            _roundStartInstalls = 0;
            lock (_gate)
            {
                _session.Reset();
                _pending.Clear();
                _pendingCasts.Clear();
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
                        if (errId == 0) Enqueue(model?.PlayerId ?? 0, model?.SkillCds);
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

        // ---------- 数据源 ----------

        /// <summary>网络线程: 只拷贝消息内容, 不读游戏对象。</summary>
        private static void Enqueue(long ownerPlayerId, MapField<int, int> cooldowns)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed || cooldowns == null) return;
            var copy = new KeyValuePair<int, int>[cooldowns.Count];
            int index = 0;
            foreach (var pair in cooldowns) copy[index++] = pair;
            lock (tracker._gate)
            {
                tracker._pending.Add(new Snapshot { OwnerPlayerId = ownerPlayerId, Cooldowns = copy });
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
            }
        }

        /// <summary>网络线程: 记下释放者, 主线程刷新时再去读游戏刚写入的冷却值。</summary>
        private static void OnSkillUsed(long playerId, int skillId)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed) return;
            lock (tracker._gate)
            {
                if (!tracker._pendingCasts.Contains(playerId)) tracker._pendingCasts.Add(playerId);
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
                lock (_gate) _session.ApplyFrame(item.OwnerPlayerId, ownerSkillId, item.Cooldowns);
                LogUnknownOwner(item);
            }
        }

        /// <summary>
        /// 主线程: 释放技能后读释放者的实时字段。游戏在技能演出前就把这一帧的 SkillCds 写进去,
        /// 所以这就是本次生效的冷却上限; 读不到(被别的帧刷成 0)就等该玩家自己的下一帧。
        /// </summary>
        private void ResolveCasts()
        {
            long[] casts;
            lock (_gate)
            {
                if (_pendingCasts.Count == 0) return;
                casts = _pendingCasts.ToArray();
                _pendingCasts.Clear();
            }
            for (int i = 0; i < casts.Length; i++)
            {
                var property = GetPlayerData(casts[i])?.Property;
                if (property == null) continue;
                int applied;
                try { applied = property.activeSkillCD.Value; } catch { continue; }
                if (applied <= 0 || !SkillCooldownRules.IsPlausible(applied)) continue;
                lock (_gate) _session.ApplyCast(casts[i], applied);
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
        /// 主线程: 战斗开始时服务器帧可能早于挂钩安装, 用每个角色自带的 Hero.SkillCds(按玩家独立)
        /// 补一次初值, 避免整个第一轮没有数字。已有服务器帧的玩家不会被覆盖。
        /// </summary>
        private void SeedMissing()
        {
            var battle = _battle;
            if (battle?.PlayerDatas == null) return;
            for (int i = 0; i < battle.PlayerDatas.Count; i++)
            {
                var player = battle.PlayerDatas[i]?.player;
                if (player == null) continue;
                long playerId = player.Id;
                var table = _session;
                bool known;
                lock (_gate) known = table.TryGetRemaining(playerId, 0, out _);
                if (known) continue;
                int skillId;
                try { skillId = player.GetBattleActiveSkillId(); } catch { continue; }
                MapField<int, int> cds;
                try { cds = player.Hero?.SkillCds; } catch { continue; }
                if (cds == null || cds.Count == 0) continue;
                lock (_gate) _session.ApplyFrame(playerId, skillId, cds);
            }
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

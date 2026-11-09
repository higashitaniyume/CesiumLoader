using System;
using System.Collections.Generic;
using Core.Net;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using GameLogic;
using party.protocol;
using Tools;

namespace CombatOddsMod
{
    /// <summary>
    /// 主动技能冷却的数据源: 只有<b>服务器查询</b>。
    ///
    ///   1. 每当有角色出牌/释放技能/新回合开始, 就向服务器查询一次<b>所有人</b>的当前冷却
    ///      (<c>GetHeroInfoC2S</c> → <c>GetHeroInfoS2C.Cd</c>, 与游戏自己的玩家信息面板同一条请求);
    ///   2. 释放技能帧顺手记下本次生效的冷却上限, 作为分式分母(没释放过时用配表值)。
    ///   3. 观战/回放时不发查询、不显示数字(回放里发 C2S 无意义，见 <see cref="IsReplayOrWatcher"/>）。
    ///
    /// 不做任何本地推算: 不做轮次递减、不读回合帧数值、不猜队友的值; 新回合只当触发时机。
    /// 查询是只读的(只返回击杀/承伤/治疗与当前冷却), 不改对局状态。
    ///
    /// 线程: 回包在网络线程只拷贝数值入队, 覆盖层刷新(主线程)时再写进会话。
    /// </summary>
    internal sealed class SkillCooldownTracker : IDisposable
    {
        private readonly SkillCooldownSession _session = new SkillCooldownSession();
        private readonly object _gate = new object();
        private readonly List<KeyValuePair<long, int>> _pendingServerCd = new List<KeyValuePair<long, int>>();
        private readonly List<KeyValuePair<long, int>> _pendingCasts = new List<KeyValuePair<long, int>>();
        private readonly List<string> _pendingLogs = new List<string>();

        /// <summary>把每条数据与最终显示值写进日志(排查显示问题用)。</summary>
        internal bool LogDetail;

        /// <summary>一批查询之间至少间隔多久(秒): 连续出牌时合并成一批, 避免刷屏。</summary>
        private const float QueryBatchInterval = 0.2f;

        private bool _queryRequested = true;
        private float _lastQueryBatchAt = -999f;
        private BattleLogic _battle;
        private int _lastRound;
        private bool _warned;
        private bool _disposed;

        // 挂钩是进程级的: 同一时刻只跟随最后启动的这个追踪器。
        private static SkillCooldownTracker _active;
        private static GetHeroInfoS2CRPC.OnGetHeroInfoS2CServerDelegate _ourHeroInfo;
        private static GetHeroInfoS2CRPC.OnGetHeroInfoS2CServerDelegate _heroInfoOver;
        private const int MaxHeroInfoInstalls = 4;
        private static int _heroInfoInstalls;

        internal void Start()
        {
            _active = this;
            try
            {
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
            try { GameEvents.SkillUsed -= OnSkillUsed; } catch { }
            if (ReferenceEquals(_active, this)) _active = null;
            UnhookHeroInfo();
            lock (_gate)
            {
                _session.Reset();
                _pendingServerCd.Clear();
                _pendingCasts.Clear();
            }
            _battle = null;
            _lastRound = 0;
        }

        /// <summary>下一次刷新时向服务器查询所有人的冷却。</summary>
        internal void RequestQueryAll()
        {
            lock (_gate) _queryRequested = true;
        }

        /// <summary>该玩家当前的剩余冷却; 还没有查询结果时返回 false。</summary>
        internal bool TryGetRemaining(long playerId, out int remaining)
        {
            lock (_gate) return _session.TryGetRemaining(playerId, out remaining);
        }

        /// <summary>该玩家这个主动技能最近一次释放时的生效上限; 没有时返回 false。</summary>
        internal bool TryGetCap(long playerId, int skillId, out int cap)
        {
            lock (_gate) return _session.TryGetCap(playerId, skillId, out cap);
        }

        /// <summary>随覆盖层刷新调用(主线程, 0.1 s 一次): 挂钩、查询、写回。</summary>
        internal void Poll()
        {
            SyncBattle();
            TrackRound();
            EnsureHeroInfoHook();
            ResolveCasts();
            QueryAllIfRequested();
            DrainServerCd();
            FlushLogs();
        }

        /// <summary>观战/回放判定: 此时不发 CD 查询、也不显示数字。</summary>
        internal static bool IsReplayOrWatcher()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null) return false;
                try { if (gm.replay != null && gm.replay.Session != null && gm.replay.Session.IsReplay) return true; }
                catch { }
                try { if (gm.watch != null && gm.watch.PlayerIsWatcher()) return true; }
                catch { }
                return false;
            }
            catch { return false; }
        }

        internal void Log(string message)
        {
            if (!LogDetail || string.IsNullOrEmpty(message)) return;
            lock (_gate) _pendingLogs.Add(message);
        }

        // ---------- 查询 ----------

        /// <summary>
        /// 主线程: 给所有玩家发一次查询。出牌/释放技能/新回合开始后调用 RequestQueryAll 触发;
        /// 连续触发时按 <see cref="QueryBatchInterval"/> 合并成一批。
        /// 观战/回放时直接丢弃请求，不发 C2S。
        /// </summary>
        private void QueryAllIfRequested()
        {
            bool requested;
            lock (_gate) requested = _queryRequested;
            if (!requested) return;
            if (IsReplayOrWatcher())
            {
                lock (_gate) _queryRequested = false;
                return;
            }
            var battle = _battle;
            if (battle?.PlayerDatas == null || battle.PlayerDatas.Count == 0) return;
            var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.GetHeroInfoC2S;
            if (rpc == null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastQueryBatchAt < QueryBatchInterval) return;   // 留在 pending, 下一帧再来
            _lastQueryBatchAt = now;
            lock (_gate) _queryRequested = false;

            for (int i = 0; i < battle.PlayerDatas.Count; i++)
            {
                var data = battle.PlayerDatas[i];
                var player = data?.player;
                if (player == null) continue;
                try { if (data.characterType != CharacterType.Hero) continue; } catch { continue; }
                long playerId = player.Id;
                if (playerId == 0) continue;
                try { rpc.GetHeroInfoC2SCall(new GetHeroInfoC2S { PlayerId = playerId }); } catch { }
                Log("CD查询 player=" + playerId);
            }
        }

        /// <summary>网络线程: 只拷贝回包里的玩家与冷却值。</summary>
        private static void EnqueueServerCd(long playerId, int cd)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed || playerId == 0) return;
            lock (tracker._gate) tracker._pendingServerCd.Add(new KeyValuePair<long, int>(playerId, cd));
        }

        /// <summary>主线程: 把回包写入会话。</summary>
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
                lock (_gate) _session.ApplyQuery(items[i].Key, items[i].Value);
                Log("CD回包 player=" + items[i].Key + " cd=" + items[i].Value);
            }
        }

        /// <summary>
        /// 包装 <c>GetHeroInfoS2C</c> 的回包。这条 RPC 不在 SDK 的包装清单里,
        /// 按 HookChain 的"只装一次 + 捕获原始委托"策略安装, 并照常转给游戏自己的处理。
        /// </summary>
        private static void EnsureHeroInfoHook()
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed) return;
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
                if (tracker != null) tracker.WarnOnce("玩家信息(CD 查询)挂钩失败: " + e.Message);
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

        // ---------- 上限(分母) ----------

        /// <summary>网络线程: 记下释放者与其技能, 主线程刷新时读游戏刚写入的本次生效上限。</summary>
        private static void OnSkillUsed(long playerId, int skillId)
        {
            var tracker = _active;
            if (tracker == null || tracker._disposed) return;
            var key = new KeyValuePair<long, int>(playerId, skillId);
            lock (tracker._gate)
            {
                if (!tracker._pendingCasts.Contains(key)) tracker._pendingCasts.Add(key);
            }
            tracker.RequestQueryAll();
        }

        /// <summary>主线程: 释放技能后游戏把该次上限写进了 <c>Property.activeSkillCD</c>。</summary>
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
                try { if (data.characterType != CharacterType.Hero) continue; } catch { continue; }
                int applied;
                try { applied = property.activeSkillCD.Value; } catch { continue; }
                Log("CD释放 player=" + playerId + " skill=" + skillId + " applied=" + applied);
                if (applied <= 0 || !SkillCooldownRules.IsPlausible(applied)) continue;
                lock (_gate) _session.ApplyCast(playerId, skillId, applied);
            }
        }

        // ---------- 辅助 ----------

        private void SyncBattle()
        {
            BattleLogic battle = null;
            try { battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle; } catch { }
            if (ReferenceEquals(battle, _battle)) return;
            _battle = battle;
            _heroInfoInstalls = 0;
            _lastQueryBatchAt = -999f;
            _lastRound = 0;
            _queryRequested = true;     // 开局/换局先查一次
            lock (_gate)
            {
                _session.Reset();
                _pendingServerCd.Clear();
                _pendingCasts.Clear();
            }
        }

        /// <summary>
        /// 新回合触发: 轮询 <c>room.curRoomInfo.Round</c>，变大时查一次 CD。
        /// 同一个 BattleLogic 复用时轮次变小视为新局，清表重查。
        /// 取不到轮次(0) 时不触发、不更新基线。
        /// </summary>
        private void TrackRound()
        {
            int round = CurrentRound();
            if (round <= 0) return;
            if (_lastRound > 0 && round < _lastRound)
            {
                lock (_gate)
                {
                    _session.Reset();
                    _pendingServerCd.Clear();
                    _pendingCasts.Clear();
                }
                _lastRound = round;
                RequestQueryAll();
                return;
            }
            if (_lastRound > 0 && round > _lastRound)
            {
                _lastRound = round;
                RequestQueryAll();
                return;
            }
            if (_lastRound == 0) _lastRound = round;
        }

        private static int CurrentRound()
        {
            try
            {
                var room = SimpleSingletonProvider<GameLogicManager>.inst?.room;
                var info = room != null ? room.curRoomInfo : null;
                return info != null ? info.Round : 0;
            }
            catch { return 0; }
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
    }
}

using System;
using System.Collections.Generic;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.model;
using party.protocol;
using Tools;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 游戏事件中枢: 封装全部 RPC 回调 hook, 把原始协议转成简单事件。
    ///
    /// 事件驱动: 订阅事件后调用一次 EnsureHooked()(或直接订阅, 首次订阅自动启动
    /// 内部挂钩循环), SDK 内部每 1 秒检查并重新包装回调 —— 游戏每场战斗会重置
    /// 回调, 检测到新回调就重新包装。mod 无需自己每秒轮询。
    ///
    /// 按需挂钩: 只有当某个 RPC 喂的事件确实有订阅者时才包装它; 订阅者清空后
    /// 若当前槽位仍是 SDK 的包装, 就还原成原回调。RPC 回调是单槽位 delegate,
    /// 这样其它观测方(其它 mod / 其它工具)不必永远排在 SDK 后面。
    ///
    /// 安全: 只在确认战斗中才挂钩, 且所有单例读取都先过 hasInstance(纯静态字段判空, 不会触发懒构造);
    /// 因此从第一帧开始轮询也不会碰到"启动早期强行构造半成品单例"的崩溃窗口。
    /// 包装后的回调会转发给游戏原回调, 不干扰游戏逻辑。
    /// </summary>
    public static class GameEvents
    {
        // ---------- 自动挂钩循环 ----------

        private static bool _autoHookStarted;

        /// <summary>
        /// 启动内部挂钩循环(每 1 秒检查战斗状态并重新包装 RPC 回调)。
        /// 幂等: 多次调用只启动一次。mod 不需要自己每秒轮询 —— 订阅事件后调一次即可。
        /// 不需要等待启动延迟: <see cref="EnsureHooked"/> 自身只在"已经进入战斗"时才挂钩,
        /// 且不会在游戏建好单例之前强行创建。
        /// </summary>
        public static void StartAutoHook()
        {
            if (_autoHookStarted) return;
            _autoHookStarted = true;
            try
            {
                AutoHookLoopAsync().Forget();
            }
            catch { }
        }

        private static async UniTaskVoid AutoHookLoopAsync()
        {
            while (true)
            {
                try { EnsureHooked(); }
                catch { }
                try { await UniTask.Delay(1000); } catch { return; }
            }
        }

        // ---------- 事件 ----------

        /// <summary>战斗用牌: (playerId, 真实cardId, 剩余手牌数)。CardId 已从 Guid 反查。</summary>
        public static event Action<long, int, int> CardUsed;
        /// <summary>无牌可出跳过: (playerId)。</summary>
        public static event Action<long> NoCard;
        /// <summary>棋盘效果牌: (playerId, cardId, 剩余手牌数)。</summary>
        public static event Action<long, int, int> EffectCardUsed;
        /// <summary>技能释放: (playerId, skillId)。</summary>
        public static event Action<long, int> SkillUsed;
        /// <summary>快速卡/跟牌: (playerId, cardId, 被跟的originalCardId)。</summary>
        public static event Action<long, int, int> QuickCardUsed;
        /// <summary>掷骰子: (playerId, point, maxPoint)。</summary>
        public static event Action<long, int, int> DiceResult;
        /// <summary>移动: (playerId, 步数, 是否到达)。</summary>
        public static event Action<long, int, bool> Move;
        /// <summary>战斗更新(攻防变化/结束时触发, 已去重): (Battle)。</summary>
        public static event Action<Battle> BattleUpdate;
        /// <summary>战斗攻击骰子: (playerId, 点数)。</summary>
        public static event Action<long, int> BattleDice;
        /// <summary>
        /// 战斗子阶段变化: (battle, phase)。
        ///
        /// 攻方投出骰子的瞬间触发一次（包括投出 0 点）。投出 0 点时战斗数值可能完全不变,
        /// <see cref="BattleUpdate"/> 不会触发, 需要立刻重算的订阅方应同时处理本事件。
        /// </summary>
        public static event Action<Battle, BattlePhase> BattlePhaseChanged;
        /// <summary>回合结束选奖励卡: (playerId, cardId)。</summary>
        public static event Action<long, int> RewardCardSelected;
        /// <summary>商店待选卡: (playerId, cardId列表)。</summary>
        public static event Action<long, IReadOnlyList<int>> ShopCandidates;
        /// <summary>筹码格候选遗物: (playerId, relicId列表)。</summary>
        public static event Action<long, IReadOnlyList<int>> RelicCandidates;
        /// <summary>筹码选择结果: (playerId, relicId)。只上报"真正选定", 刷新不会触发本事件。</summary>
        public static event Action<long, int> RelicSelected;
        /// <summary>遗物同步(开局/变更): (playerId, relicId列表)。</summary>
        public static event Action<long, IReadOnlyList<int>> RelicsSynced;
        /// <summary>手牌变化: (playerId, 原始CardInfo列表)。队友的 CardId 可能是负数(服务器掩码)。</summary>
        public static event Action<long, IReadOnlyList<CardInfo>> HandChanged;

        /// <summary>
        /// 筹码结果(完整): (playerId, relicId, isReroll)。
        ///
        /// <c>isReroll=true</c> 表示这次只是刷新(玩家还没选), <c>false</c> 才是真正选定。
        /// <see cref="RelicSelected"/> 保持"只报选定"的旧语义不变, 需要区分刷新与选定时用本事件。
        /// 刷新次数用 <see cref="RelicRerollChanged"/> 或 <see cref="RelicRerollRemaining"/> 读。
        /// </summary>
        public static event Action<long, int, bool> RelicResult;

        /// <summary>
        /// 筹码剩余刷新次数变化: (playerId, 剩余刷新次数)。
        ///
        /// 服务端把刷新次数放在 1040 属性更新的 <c>Reroll</c> 项里推下来。
        /// 初始化时用 <see cref="RelicRerollRemaining"/> 拉一次, 之后靠本事件增量更新。
        /// </summary>
        public static event Action<long, int> RelicRerollChanged;

        /// <summary>
        /// 血量变化: (playerId, 变化量, 最大血量)。
        ///
        /// <b>变化量是差值, 可正可负, 不是绝对血量</b>(正数通常是回复, 负数是受伤)。
        /// 累加即可得到累计伤害; 血量归零不等于击杀, 击杀归属看 <see cref="PlayerKilled"/>。
        /// </summary>
        public static event Action<long, int, int> HeroHpChanged;

        /// <summary>
        /// 击杀: (victimId, attackerId)。
        ///
        /// 只在这条消息里才有攻击者归属 —— 毒、灼烧、地图伤害等掉血不会有击杀消息。
        /// </summary>
        public static event Action<long, long> PlayerKilled;

        /// <summary>
        /// 商店成交确认(PVE 5216 / PVP 5030), 参数是 <see cref="ShopSettlement"/>。
        ///
        /// 与"进店报价"不同, 这里才知道到底买了什么、是否离店、是否在给队友转账
        /// (<see cref="ShopSettlement.AssistPlayer"/> &gt; 0 且 <see cref="ShopSettlement.BuyCards"/> 为空)。
        /// </summary>
        public static event Action<ShopSettlement> ShopSettled;

        /// <summary>商店报价(完整字段): 参数是 <see cref="ShopOffer"/>。比 <see cref="ShopCandidates"/> 多价格/折扣/免费/已买。</summary>
        public static event Action<ShopOffer> ShopOffered;

        /// <summary>筹码候选(完整字段): 参数是 <see cref="RelicOffer"/>。比 <see cref="RelicCandidates"/> 多 Sn 与刷新标记。</summary>
        public static event Action<RelicOffer> RelicOffered;

        /// <summary>
        /// 匹配成功: 服务器已经为本局建好房间。参数是房间快照(可能为 null)。
        /// 触发时房间已建好并切到选人(CHOICE)状态; 匹配成功动画结束后才会打开选人界面。
        /// </summary>
        public static event Action<Room> MatchSuccess;

        /// <summary>
        /// 进入选人阶段: 选人界面已经打开, 可以调用 <see cref="GameActions.SelectHero"/>。
        /// 匹配成功动画结束后, 或房主开始游戏后服务器要求玩家选角色时触发。
        /// 参数是房间快照(可能为 null)。同一次匹配只会触发一次; 重开一局会再次触发。
        /// </summary>
        public static event Action<Room> HeroSelectionStarted;

        /// <summary>
        /// 玩家确认角色: (playerId, heroId, heroTaken)。
        /// <c>heroTaken=true</c> 表示该角色已被其他玩家占用, 服务器拒绝了这次确认
        /// (客户端会提示并让玩家重选)。
        /// </summary>
        public static event Action<long, int, bool> HeroSelected;

        /// <summary>实时属性更新(含 CauseOrigin、生命变化和 DamageType)。</summary>
        public static event Action<UpdateHeroAttrS2C> HeroAttrUpdated;

        /// <summary>原始动作流, 不做过滤也不去重。
        ///
        /// 用途: SDK 只把常见的几类动作翻译成上面那些强类型事件(如商店/筹码候选),
        /// 但有些窗口没有对应事件(筹码地块购买 offer 5249、移动 5027 的目标地块、
        /// 奖励卡候选等)。需要"看到全部动作"的 mod 用这个事件自己解码
        /// (Data 是 protobuf payload, 用 <c>Core.Net.ByteBuf.ReadObject&lt;T&gt;</c> 解)。
        ///
        /// 注意:
        ///   - 回调在**网络线程**触发, 订阅方自己保证线程安全(别在里面碰 Unity API);
        ///   - 只在有订阅者时才拷贝 <see cref="RawActionEvent.Data"/>(没订阅者零分配);
        ///   - 订阅方抛异常不会影响游戏与其他订阅者(SDK 逐个 try/catch)。
        /// </summary>
        public static event Action<RawActionEvent> RawAction;

        /// <summary>已定义的事件数量(静态常量, 含 <see cref="RawAction"/>)。</summary>
        public static int EventCount => 28;

        /// <summary>当前战斗的子阶段(由战斗快照与投骰广播的先后顺序推导)。</summary>
        public static BattlePhase CurrentBattlePhase => _battlePhase.Phase;

        /// <summary>当前战斗的攻方是否已经投骰。</summary>
        public static bool AttackerHasRolled => _battlePhase.AttackerHasRolled;

        /// <summary>
        /// 当前战斗攻方的骰点: 尚未投骰时为 -1, 已投骰时为 0..6。
        ///
        /// 0 是有效点数(例如摩西【破绽】把怪物战斗骰点压到 0), 不能用
        /// <c>BattleRole.Point &gt; 0</c> 判断骰子是否已经落地 —— 未投骰时该字段同样是 0。
        /// 战斗结束后保留该场骰点, 直到下一场战斗开始。
        /// </summary>
        public static int AttackerPoint => _battlePhase.AttackerPoint;

        // ---------- 查询 ----------

        /// <summary>
        /// 读某个结果消息当前对应的下行序号(用于和候选 action 的 <c>Sn</c> 对账);
        /// 读不到返回 0。命令号见 <see cref="GameCommands"/>。
        ///
        /// 必须在对应事件的处理器里<b>同步</b>调用: 游戏是"把消息放在队首、未标记消费时"
        /// 调用回调的, 离开回调后队首就换成下一条了。
        /// </summary>
        public static long DownSn(int command)
        {
            try
            {
                if (!MonoSingletonProvider<NetManager>.hasInstance) return 0;
                return DownSn(MonoSingletonProvider<NetManager>.inst?.RPC, command);
            }
            catch { return 0; }
        }

        /// <summary>读指定 RPC 管理器上某命令的"最新未消费"下行序号; 读不到返回 0。</summary>
        public static long DownSn(Core.Net.RPCMsgManager rpc, int command)
        {
            return DownSnReader.Read(rpc, command);
        }

        /// <summary>
        /// 筹码剩余刷新次数。查不到(不在战斗/没有该玩家/没有该属性)返回 <b>-1</b>,
        /// 不会返回 0 —— 0 会被误判成"没次数了"。
        /// </summary>
        public static int RelicRerollRemaining(long playerId)
        {
            try
            {
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return -1;
                var pd = Players.Get(playerId);
                var count = pd?.Property?.reRelicCount;
                if (count == null) return -1;
                return count.Value;
            }
            catch { return -1; }
        }

        // ---------- hook 状态 ----------

        private static readonly HashSet<long> _reportedShopSn = new HashSet<long>();
        private static readonly HashSet<long> _reportedRelicSn = new HashSet<long>();
        private static string _lastBattleKey;
        private static readonly BattlePhaseTracker _battlePhase = new BattlePhaseTracker();
        private static Battle _currentBattle;
        private static bool _hookSummaryLogged;

        /// <summary>
        /// 一个 RPC 回调槽位的包装状态。
        ///
        /// 槽位是单 delegate: 谁要观测都得"存下当前值 → 换成自己的包装 → 包装里再调回去"。
        /// 这里统一处理三件事: 幂等安装、按需还原、以及"别人的包装压在我们上面"时不要重复包装。
        /// </summary>
        private sealed class HookSlot<T> where T : class
        {
            private T _wrapped;
            private T _original;
            private object _owner;

            /// <summary>本次包装应当调用的原回调(安装时捕获, 不能改读可变静态字段, 否则会自我递归)。</summary>
            public T Original => _original;

            /// <summary>
            /// 返回 true 表示调用方应当安装新包装; false 表示无需动作(已包装 / 不需要 / 已还原)。
            /// 不需要时若当前槽位仍是我们的包装, 会通过 <paramref name="restore"/> 还原。
            /// </summary>
            public bool Prepare(bool needed, object rpc, T current, Action<T> restore)
            {
                if (!needed)
                {
                    if (_wrapped != null)
                    {
                        if (ReferenceEquals(current, _wrapped) && restore != null) restore(_original);
                        _wrapped = null;
                        _original = null;
                        _owner = null;
                    }
                    return false;
                }

                if (current == null) return false;                       // 游戏还没注册回调
                if (_wrapped != null && ReferenceEquals(current, _wrapped)) return false;   // 已包装, 幂等

                // 同一个 RPC 实例上、当前槽位既不是我们的包装也不是我们捕获的原回调
                // => 别人在我们的包装之上又包了一层, 且多半是链式调用。再包会重复上报。
                if (_wrapped != null && ReferenceEquals(_owner, rpc) && !ReferenceEquals(current, _original))
                    return false;

                _original = current;
                _owner = rpc;
                return true;
            }

            public void Installed(T wrapped)
            {
                _wrapped = wrapped;
            }
        }

        private static readonly HookSlot<Core.Net.PredictActionS2CRPC.OnPredictActionS2CServerDelegate> _predictHook =
            new HookSlot<Core.Net.PredictActionS2CRPC.OnPredictActionS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.BattleUseCardS2CRPC.OnBattleUseCardS2CServerDelegate> _battleUseCardHook =
            new HookSlot<Core.Net.BattleUseCardS2CRPC.OnBattleUseCardS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.UseEffectCardS2CRPC.OnUseEffectCardS2CServerDelegate> _effectCardHook =
            new HookSlot<Core.Net.UseEffectCardS2CRPC.OnUseEffectCardS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.UseQuickCardS2CRPC.OnUseQuickCardS2CServerDelegate> _quickCardHook =
            new HookSlot<Core.Net.UseQuickCardS2CRPC.OnUseQuickCardS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.SelectRelicS2CRPC.OnSelectRelicS2CServerDelegate> _relicHook =
            new HookSlot<Core.Net.SelectRelicS2CRPC.OnSelectRelicS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.RoomHeroCardChangeS2CRPC.OnRoomHeroCardChangeS2CServerDelegate> _cardChangeHook =
            new HookSlot<Core.Net.RoomHeroCardChangeS2CRPC.OnRoomHeroCardChangeS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.ThrowDiceResultS2CRPC.OnThrowDiceResultS2CServerDelegate> _diceHook =
            new HookSlot<Core.Net.ThrowDiceResultS2CRPC.OnThrowDiceResultS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.SelectRewardCardS2CRPC.OnSelectRewardCardS2CServerDelegate> _rewardCardHook =
            new HookSlot<Core.Net.SelectRewardCardS2CRPC.OnSelectRewardCardS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.SyncRelicsS2CRPC.OnSyncRelicsS2CServerDelegate> _syncRelicsHook =
            new HookSlot<Core.Net.SyncRelicsS2CRPC.OnSyncRelicsS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.UpdateHeroAttrS2CRPC.OnUpdateHeroAttrS2CServerDelegate> _attrHook =
            new HookSlot<Core.Net.UpdateHeroAttrS2CRPC.OnUpdateHeroAttrS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.BattleS2CRPC.OnBattleS2CServerDelegate> _battleHook =
            new HookSlot<Core.Net.BattleS2CRPC.OnBattleS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.BattleThrowDiceS2CRPC.OnBattleThrowDiceS2CServerDelegate> _battleDiceHook =
            new HookSlot<Core.Net.BattleThrowDiceS2CRPC.OnBattleThrowDiceS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.MoveS2CRPC.OnMoveS2CServerDelegate> _moveHook =
            new HookSlot<Core.Net.MoveS2CRPC.OnMoveS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.PVEShopBuyS2CRPC.OnPVEShopBuyS2CServerDelegate> _pveShopHook =
            new HookSlot<Core.Net.PVEShopBuyS2CRPC.OnPVEShopBuyS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.ShopBuyS2CRPC.OnShopBuyS2CServerDelegate> _pvpShopHook =
            new HookSlot<Core.Net.ShopBuyS2CRPC.OnShopBuyS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.KillMessageS2CRPC.OnKillMessageS2CServerDelegate> _killHook =
            new HookSlot<Core.Net.KillMessageS2CRPC.OnKillMessageS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate> _matchSuccessHook =
            new HookSlot<Core.Net.MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.StartGameS2CRPC.OnStartGameS2CServerDelegate> _startGameHook =
            new HookSlot<Core.Net.StartGameS2CRPC.OnStartGameS2CServerDelegate>();
        private static readonly HookSlot<Core.Net.AffirmHeroS2CRPC.OnAffirmHeroS2CServerDelegate> _affirmHeroHook =
            new HookSlot<Core.Net.AffirmHeroS2CRPC.OnAffirmHeroS2CServerDelegate>();

        /// <summary>
        /// 挂钩全部 RPC(幂等: 已包装的跳过; 没人订阅的还原)。游戏每场战斗会重置回调,
        /// 所以每秒调用一次。
        /// </summary>
        public static void EnsureHooked()
        {
            // 匹配/选人发生在战斗之外, 单独挂钩(不能受下面的战斗门控限制)。
            EnsureRoomHooked();
            try
            {
                // 只在确认战斗中挂钩。两步都先 hasInstance 判空(纯静态字段, 不触发懒构造):
                // 取 .inst 会 new 一个游戏自己都不认识的半成品单例, 启动/非战斗时那样做会在原生段崩。
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm?.battle == null || gm.battle.PlayerDatas == null || gm.battle.PlayerDatas.Count == 0) return;
                if (!MonoSingletonProvider<NetManager>.hasInstance) return;
                var net = MonoSingletonProvider<NetManager>.inst?.RPC;
                if (net == null) return;

                HookPredict(net);
                HookBattleUseCard(net);
                HookEffectCard(net);
                HookQuickCard(net);
                HookRelic(net);
                HookCardChange(net);
                HookDice(net);
                HookRewardCard(net);
                HookSyncRelics(net);
                HookBattle(net);
                HookHeroAttr(net);
                HookBattleDice(net);
                HookMove(net);
                HookPveShopBuy(net);
                HookPvpShopBuy(net);
                HookKill(net);

                if (!_hookSummaryLogged)
                {
                    _hookSummaryLogged = true;
                    SdkLog.Write("EVENTS", "战斗 RPC 回调已就绪(按订阅按需安装)");
                }
            }
            catch (Exception e)
            {
                SdkLog.Write("EVENTS", "挂钩失败: " + e.Message);
            }
        }

        // ---------- 各 hook ----------

        private static void HookPredict(Core.Net.RPCMsgManager net)
        {
            var rpc = net.PredictActionS2C;
            if (rpc == null) return;

            bool needed = RawAction != null
                       || ShopCandidates != null
                       || RelicCandidates != null
                       || ShopOffered != null
                       || RelicOffered != null;
            if (!_predictHook.Prepare(needed, rpc, rpc.OnPredictActionS2CServerCallBackAsync,
                    o => rpc.OnPredictActionS2CServerCallBackAsync = o)) return;

            var original = _predictHook.Original;
            Core.Net.PredictActionS2CRPC.OnPredictActionS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnPredictActions(model?.Actions); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnPredictActionS2CServerCallBackAsync = wrapped;
            _predictHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] PredictActionS2C");
        }

        private static void HookBattleUseCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.BattleUseCardS2C;
            if (rpc == null) return;

            bool needed = CardUsed != null || NoCard != null;
            if (!_battleUseCardHook.Prepare(needed, rpc, rpc.OnBattleUseCardS2CServerCallBackAsync,
                    o => rpc.OnBattleUseCardS2CServerCallBackAsync = o)) return;

            var original = _battleUseCardHook.Original;
            Core.Net.BattleUseCardS2CRPC.OnBattleUseCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnBattleUseCard(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnBattleUseCardS2CServerCallBackAsync = wrapped;
            _battleUseCardHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] BattleUseCardS2C");
        }

        private static void HookEffectCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.UseEffectCardS2C;
            if (rpc == null) return;

            bool needed = EffectCardUsed != null || SkillUsed != null;
            if (!_effectCardHook.Prepare(needed, rpc, rpc.OnUseEffectCardS2CServerCallBackAsync,
                    o => rpc.OnUseEffectCardS2CServerCallBackAsync = o)) return;

            var original = _effectCardHook.Original;
            Core.Net.UseEffectCardS2CRPC.OnUseEffectCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnEffectCardUsed(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnUseEffectCardS2CServerCallBackAsync = wrapped;
            _effectCardHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] UseEffectCardS2C");
        }

        private static void HookQuickCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.UseQuickCardS2C;
            if (rpc == null) return;

            if (!_quickCardHook.Prepare(QuickCardUsed != null, rpc, rpc.OnUseQuickCardS2CServerCallBackAsync,
                    o => rpc.OnUseQuickCardS2CServerCallBackAsync = o)) return;

            var original = _quickCardHook.Original;
            Core.Net.UseQuickCardS2CRPC.OnUseQuickCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnQuickCardUsed(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnUseQuickCardS2CServerCallBackAsync = wrapped;
            _quickCardHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] UseQuickCardS2C");
        }

        private static void HookRelic(Core.Net.RPCMsgManager net)
        {
            var rpc = net.SelectRelicS2C;
            if (rpc == null) return;

            bool needed = RelicSelected != null || RelicResult != null;
            if (!_relicHook.Prepare(needed, rpc, rpc.OnSelectRelicS2CServerCallBackAsync,
                    o => rpc.OnSelectRelicS2CServerCallBackAsync = o)) return;

            var original = _relicHook.Original;
            Core.Net.SelectRelicS2CRPC.OnSelectRelicS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnRelicSelected(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnSelectRelicS2CServerCallBackAsync = wrapped;
            _relicHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] SelectRelicS2C");
        }

        private static void HookCardChange(Core.Net.RPCMsgManager net)
        {
            var rpc = net.RoomHeroCardChangeS2C;
            if (rpc == null) return;

            if (!_cardChangeHook.Prepare(HandChanged != null, rpc, rpc.OnRoomHeroCardChangeS2CServerCallBackAsync,
                    o => rpc.OnRoomHeroCardChangeS2CServerCallBackAsync = o)) return;

            var original = _cardChangeHook.Original;
            Core.Net.RoomHeroCardChangeS2CRPC.OnRoomHeroCardChangeS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnHeroCardChange(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnRoomHeroCardChangeS2CServerCallBackAsync = wrapped;
            _cardChangeHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] RoomHeroCardChangeS2C");
        }

        private static void HookDice(Core.Net.RPCMsgManager net)
        {
            var rpc = net.ThrowDiceResultS2C;
            if (rpc == null) return;

            if (!_diceHook.Prepare(DiceResult != null, rpc, rpc.OnThrowDiceResultS2CServerCallBackAsync,
                    o => rpc.OnThrowDiceResultS2CServerCallBackAsync = o)) return;

            var original = _diceHook.Original;
            Core.Net.ThrowDiceResultS2CRPC.OnThrowDiceResultS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnDiceResult(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnThrowDiceResultS2CServerCallBackAsync = wrapped;
            _diceHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] ThrowDiceResultS2C");
        }

        private static void HookRewardCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.SelectRewardCardS2C;
            if (rpc == null) return;

            if (!_rewardCardHook.Prepare(RewardCardSelected != null, rpc, rpc.OnSelectRewardCardS2CServerCallBackAsync,
                    o => rpc.OnSelectRewardCardS2CServerCallBackAsync = o)) return;

            var original = _rewardCardHook.Original;
            Core.Net.SelectRewardCardS2CRPC.OnSelectRewardCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnRewardCard(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnSelectRewardCardS2CServerCallBackAsync = wrapped;
            _rewardCardHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] SelectRewardCardS2C");
        }

        private static void HookSyncRelics(Core.Net.RPCMsgManager net)
        {
            var rpc = net.SyncRelicsS2C;
            if (rpc == null) return;

            if (!_syncRelicsHook.Prepare(RelicsSynced != null, rpc, rpc.OnSyncRelicsS2CServerCallBackAsync,
                    o => rpc.OnSyncRelicsS2CServerCallBackAsync = o)) return;

            var original = _syncRelicsHook.Original;
            Core.Net.SyncRelicsS2CRPC.OnSyncRelicsS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnSyncRelics(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnSyncRelicsS2CServerCallBackAsync = wrapped;
            _syncRelicsHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] SyncRelicsS2C");
        }

        private static void HookHeroAttr(Core.Net.RPCMsgManager net)
        {
            var rpc = net.UpdateHeroAttrS2C;
            if (rpc == null) return;

            bool needed = HeroAttrUpdated != null || HeroHpChanged != null || RelicRerollChanged != null;
            if (!_attrHook.Prepare(needed, rpc, rpc.OnUpdateHeroAttrS2CServerCallBackAsync,
                    o => rpc.OnUpdateHeroAttrS2CServerCallBackAsync = o)) return;

            var original = _attrHook.Original;
            Core.Net.UpdateHeroAttrS2CRPC.OnUpdateHeroAttrS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnHeroAttr(model, errId); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnUpdateHeroAttrS2CServerCallBackAsync = wrapped;
            _attrHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] UpdateHeroAttrS2C");
        }

        private static void HookBattle(Core.Net.RPCMsgManager net)
        {
            var rpc = net.BattleS2C;
            if (rpc == null) return;

            bool needed = BattleUpdate != null || BattlePhaseChanged != null;
            if (!_battleHook.Prepare(needed, rpc, rpc.OnBattleS2CServerCallBackAsync,
                    o => rpc.OnBattleS2CServerCallBackAsync = o)) return;

            var original = _battleHook.Original;
            Core.Net.BattleS2CRPC.OnBattleS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnBattle(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnBattleS2CServerCallBackAsync = wrapped;
            _battleHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] BattleS2C");
        }

        private static void HookBattleDice(Core.Net.RPCMsgManager net)
        {
            var rpc = net.BattleThrowDiceS2C;
            if (rpc == null) return;

            bool needed = BattleDice != null || BattlePhaseChanged != null;
            if (!_battleDiceHook.Prepare(needed, rpc, rpc.OnBattleThrowDiceS2CServerCallBackAsync,
                    o => rpc.OnBattleThrowDiceS2CServerCallBackAsync = o)) return;

            var original = _battleDiceHook.Original;
            Core.Net.BattleThrowDiceS2CRPC.OnBattleThrowDiceS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnBattleDice(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnBattleThrowDiceS2CServerCallBackAsync = wrapped;
            _battleDiceHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] BattleThrowDiceS2C");
        }

        private static void HookMove(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MoveS2C;
            if (rpc == null) return;

            if (!_moveHook.Prepare(Move != null, rpc, rpc.OnMoveS2CServerCallBackAsync,
                    o => rpc.OnMoveS2CServerCallBackAsync = o)) return;

            var original = _moveHook.Original;
            Core.Net.MoveS2CRPC.OnMoveS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnMove(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnMoveS2CServerCallBackAsync = wrapped;
            _moveHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] MoveS2C");
        }

        private static void HookPveShopBuy(Core.Net.RPCMsgManager net)
        {
            var rpc = net.PVEShopBuyS2C;
            if (rpc == null) return;

            if (!_pveShopHook.Prepare(ShopSettled != null, rpc, rpc.OnPVEShopBuyS2CServerCallBackAsync,
                    o => rpc.OnPVEShopBuyS2CServerCallBackAsync = o)) return;

            var original = _pveShopHook.Original;
            Core.Net.PVEShopBuyS2CRPC.OnPVEShopBuyS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try
                {
                    if (errId == 0 && model != null) Emit(ShopSettled, BuildPveSettlement(model, net));
                }
                catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnPVEShopBuyS2CServerCallBackAsync = wrapped;
            _pveShopHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] PVEShopBuyS2C");
        }

        private static void HookPvpShopBuy(Core.Net.RPCMsgManager net)
        {
            var rpc = net.ShopBuyS2C;
            if (rpc == null) return;

            if (!_pvpShopHook.Prepare(ShopSettled != null, rpc, rpc.OnShopBuyS2CServerCallBackAsync,
                    o => rpc.OnShopBuyS2CServerCallBackAsync = o)) return;

            var original = _pvpShopHook.Original;
            Core.Net.ShopBuyS2CRPC.OnShopBuyS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try
                {
                    if (errId == 0 && model != null) Emit(ShopSettled, BuildPvpSettlement(model, net));
                }
                catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnShopBuyS2CServerCallBackAsync = wrapped;
            _pvpShopHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] ShopBuyS2C");
        }

        private static void HookKill(Core.Net.RPCMsgManager net)
        {
            var rpc = net.KillMessageS2C;
            if (rpc == null) return;

            if (!_killHook.Prepare(PlayerKilled != null, rpc, rpc.OnKillMessageS2CServerCallBackAsync,
                    o => rpc.OnKillMessageS2CServerCallBackAsync = o)) return;

            var original = _killHook.Original;
            Core.Net.KillMessageS2CRPC.OnKillMessageS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try
                {
                    if (errId == 0 && model != null) Emit(PlayerKilled, model.DefenderId, model.AttackerId);
                }
                catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnKillMessageS2CServerCallBackAsync = wrapped;
            _killHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] KillMessageS2C");
        }

        // ---------- 匹配 / 选人(战斗之外) ----------

        /// <summary>
        /// 挂钩匹配成功与选人相关的 RPC(幂等, 按需)。
        ///
        /// 这些消息在战斗之外到达, 所以不走 <see cref="EnsureHooked"/> 的战斗门控;
        /// 配套回调同样会被游戏在重新进入匹配/房间时重置, 需要周期性调用
        /// (StartAutoHook 已经包含本方法)。
        /// </summary>
        public static void EnsureRoomHooked()
        {
            try
            {
                if (!MonoSingletonProvider<NetManager>.hasInstance) return;
                var net = MonoSingletonProvider<NetManager>.inst?.RPC;
                if (net == null) return;
                HookMatchSuccess(net);
                HookStartGame(net);
                HookAffirmHero(net);
            }
            catch (Exception e)
            {
                SdkLog.Write("EVENTS", "匹配/选人挂钩失败: " + e.Message);
            }
        }

        private static void HookMatchSuccess(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MatchSuccessS2C;
            if (rpc == null) return;

            bool needed = MatchSuccess != null || HeroSelectionStarted != null;
            if (!_matchSuccessHook.Prepare(needed, rpc, rpc.OnMatchSuccessS2CServerCallBackAsync,
                    o => rpc.OnMatchSuccessS2CServerCallBackAsync = o)) return;

            var original = _matchSuccessHook.Original;
            Core.Net.MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                // 先让游戏同步建好房间并切到选人状态, 再立即通知"匹配成功"(不等 5 秒的匹配动画)。
                UniTask pending = UniTask.CompletedTask;
                try
                {
                    if (original != null) pending = original(model, errId, isDispatch);
                }
                catch (Exception e)
                {
                    SdkLog.Write("EVENTS", "MatchSuccessS2C 原回调异常: " + e.Message);
                }

                if (errId != 0) return pending;

                var room = model != null ? model.Room : null;
                Emit(MatchSuccess, room);
                // 选人界面在匹配成功动画之后才打开, 因此这条事件等原回调完成后触发。
                return RaiseAfter(pending, () => Emit(HeroSelectionStarted, room));
            };
            rpc.OnMatchSuccessS2CServerCallBackAsync = wrapped;
            _matchSuccessHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] MatchSuccessS2C");
        }

        private static void HookStartGame(Core.Net.RPCMsgManager net)
        {
            var rpc = net.StartGameS2C;
            if (rpc == null) return;

            if (!_startGameHook.Prepare(HeroSelectionStarted != null, rpc, rpc.OnStartGameS2CServerCallBackAsync,
                    o => rpc.OnStartGameS2CServerCallBackAsync = o)) return;

            var original = _startGameHook.Original;
            Core.Net.StartGameS2CRPC.OnStartGameS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                var pending = original != null ? original(model, errId, isDispatch) : UniTask.CompletedTask;
                return RaiseAfter(pending, () =>
                {
                    if (errId != 0 || model == null || model.Room == null) return;
                    Emit(HeroSelectionStarted, model.Room);
                });
            };
            rpc.OnStartGameS2CServerCallBackAsync = wrapped;
            _startGameHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] StartGameS2C");
        }

        private static void HookAffirmHero(Core.Net.RPCMsgManager net)
        {
            var rpc = net.AffirmHeroS2C;
            if (rpc == null) return;

            if (!_affirmHeroHook.Prepare(HeroSelected != null, rpc, rpc.OnAffirmHeroS2CServerCallBackAsync,
                    o => rpc.OnAffirmHeroS2CServerCallBackAsync = o)) return;

            var original = _affirmHeroHook.Original;
            Core.Net.AffirmHeroS2CRPC.OnAffirmHeroS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                var pending = original != null ? original(model, errId, isDispatch) : UniTask.CompletedTask;
                return RaiseAfter(pending, () =>
                {
                    if (errId != 0 || model == null) return;
                    Emit(HeroSelected, model.PlayerId, model.HeroId, model.HasChoice);
                });
            };
            rpc.OnAffirmHeroS2CServerCallBackAsync = wrapped;
            _affirmHeroHook.Installed(wrapped);
            SdkLog.Write("EVENTS", "[挂钩] AffirmHeroS2C");
        }

        /// <summary>先让游戏处理完原始消息, 再通知订阅者; 两边都不让异常外溢。</summary>
        private static async UniTask RaiseAfter(UniTask original, System.Action raise)
        {
            try { await original; } catch { }
            try { if (raise != null) raise(); } catch { }
        }

        // ---------- 事件触发 ----------

        /// <summary>逐个订阅者 try/catch: 一个 mod 抛异常不能影响其它订阅者, 更不能影响游戏。</summary>
        private static void Emit<T>(Action<T> handler, T value)
        {
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<T>)list[i])(value); } catch { }
            }
        }

        private static void Emit<T1, T2>(Action<T1, T2> handler, T1 a, T2 b)
        {
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<T1, T2>)list[i])(a, b); } catch { }
            }
        }

        private static void Emit<T1, T2, T3>(Action<T1, T2, T3> handler, T1 a, T2 b, T3 c)
        {
            if (handler == null) return;
            var list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<T1, T2, T3>)list[i])(a, b, c); } catch { }
            }
        }

        private static void OnBattleUseCard(BattleUseCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            if (model.CardId != 0)
            {
                int realCardId = Players.ResolveCardGuid(model.PlayerId, model.CardId);
                int remain = Players.HandCount(model.PlayerId);
                Emit(CardUsed, model.PlayerId, realCardId, remain);
            }
            else if (model.NoCard)
            {
                Emit(NoCard, model.PlayerId);
            }
        }

        private static void OnEffectCardUsed(UseEffectCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            if (model.UseSkill)
            {
                Emit(SkillUsed, model.PlayerId, model.SkillId);
                return;
            }
            if (model.CardId != 0)
            {
                int remain = Players.HandCount(model.PlayerId);
                Emit(EffectCardUsed, model.PlayerId, model.CardId, remain);
            }
        }

        private static void OnQuickCardUsed(UseQuickCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.CardId == 0) return;
            Emit(QuickCardUsed, model.PlayerId, model.CardId, model.OriginalCardId);
        }

        private static void OnRelicSelected(SelectRelicS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;

            if (model.IsReroll)
            {
                // 刷新: 只走新事件, 旧 RelicSelected 保持"只报选定"的语义。
                Emit(RelicResult, model.PlayerId, model.RelicId, true);
                return;
            }

            if (model.RelicId == 0) return;
            Emit(RelicSelected, model.PlayerId, model.RelicId);
            Emit(RelicResult, model.PlayerId, model.RelicId, false);
        }

        private static void OnHeroCardChange(RoomHeroCardChangeS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.Cards == null) return;
            Emit<long, IReadOnlyList<CardInfo>>(HandChanged, model.PlayerId, model.Cards);
        }

        private static void OnDiceResult(ThrowDiceResultS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            Emit(DiceResult, model.PlayerId, model.Point, model.MaxPoint);
        }

        private static void OnRewardCard(SelectRewardCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.CardId == 0) return;
            Emit(RewardCardSelected, model.PlayerId, model.CardId);
        }

        private static void OnSyncRelics(SyncRelicsS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.SelectRelics == null) return;
            var ids = new List<int>();
            foreach (var kv in model.SelectRelics)
                if (kv.Value && kv.Key > 0) ids.Add(kv.Key);
            if (ids.Count > 0) Emit<long, IReadOnlyList<int>>(RelicsSynced, model.PlayerId, ids);
        }

        /// <summary>
        /// 属性更新: 一条消息里可能同时带血量变化与筹码刷新次数, 逐个 effect 解出来再广播。
        /// 血量是"差值"(<c>ChangeHp</c>), 不是绝对血量。
        /// </summary>
        private static void OnHeroAttr(UpdateHeroAttrS2C model, int errId)
        {
            if (model == null || errId != 0) return;

            Emit(HeroAttrUpdated, model);

            if (HeroHpChanged == null && RelicRerollChanged == null) return;
            var effects = model.EffectDatas;
            if (effects == null) return;
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (effect == null) continue;

                var hp = effect.Hp;
                if (hp != null)
                    Emit(HeroHpChanged, UnitId(model.PlayerId, hp.PlayerId), hp.ChangeHp, hp.MaxHp);

                var reroll = effect.Reroll;
                if (reroll != null)
                    Emit(RelicRerollChanged, UnitId(model.PlayerId, reroll.PlayerId), reroll.ReRollNum);
            }
        }

        private static long UnitId(long fallback, long value)
        {
            return value != 0 ? value : fallback;
        }

        private static void OnBattle(BattleS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.Battle == null) return;
            var b = model.Battle;
            if (b.Attacker == null || b.Defender == null) return;

            BattlePhase before = _battlePhase.Phase;
            bool newBattle = _battlePhase.ObserveBattle(
                b.BattleId, b.Attacker.PlayerId, b.Defender.PlayerId, b.IsEnd, b.Attacker.Point);
            if (newBattle) _lastBattleKey = null;   // 新战斗即使数值相同也必须重新通知
            _currentBattle = b.IsEnd ? null : b;

            PublishBattleUpdate(b);

            if (_battlePhase.Phase != before) RaiseBattlePhaseChanged(b);
        }

        private static void OnBattleDice(BattleThrowDiceS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;

            bool changed = _battlePhase.ObserveAttackerDice(model.PlayerId, model.Val);
            Emit(BattleDice, model.PlayerId, model.Val);
            // 投出 0 点时战斗数值可能不变(BattleUpdate 会被去重), 这里补一次阶段变化通知。
            if (changed)
            {
                // 同步去重键: 随后到达的同值快照不必再触发一次 BattleUpdate。
                if (_currentBattle != null) _lastBattleKey = BattleKey(_currentBattle);
                RaiseBattlePhaseChanged(_currentBattle);
            }
        }

        /// <summary>
        /// 按"阶段 + 战斗标识 + 攻防数值 + 结束状态"去重后通知 <see cref="BattleUpdate"/>。
        /// </summary>
        private static void PublishBattleUpdate(Battle b)
        {
            if (b == null || b.Attacker == null || b.Defender == null) return;
            string key = BattleKey(b);
            if (key == _lastBattleKey) return;
            _lastBattleKey = key;
            Emit(BattleUpdate, b);
        }

        private static string BattleKey(Battle b)
        {
            return _battlePhase.Phase + "|" + b.BattleId + "|" +
                   b.Attacker.PlayerId + "|" + b.Defender.PlayerId + "|" +
                   b.Attacker.Point + "|" + b.Defender.Point + "|" +
                   b.Attacker.Atk + "|" + b.Defender.Def + "|" + b.IsEnd;
        }

        private static void RaiseBattlePhaseChanged(Battle b)
        {
            if (b == null) return;
            Emit(BattlePhaseChanged, b, _battlePhase.Phase);
        }

        private static void OnMove(MoveS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.NodeIds == null || model.NodeIds.Count == 0) return;
            Emit(Move, model.PlayerId, model.NodeIds.Count, model.End);
        }

        private static void OnPredictActions(Google.Protobuf.Collections.RepeatedField<party.model.Action> actions)
        {
            if (actions == null) return;

            bool wantShop = ShopCandidates != null || ShopOffered != null;
            bool wantRelic = RelicCandidates != null || RelicOffered != null;

            foreach (var a in actions)
            {
                if (a == null) continue;
                EmitRawAction(a);
                if ((!wantShop && !wantRelic) || a.Data == null || a.Data.Length == 0) continue;
                try
                {
                    switch (a.Id)
                    {
                        case 5029: // PVP 商店(Payload: ShopBuyC2S)
                            {
                                if (!wantShop || _reportedShopSn.Contains(a.Sn)) break;
                                var d = ByteBuf.ReadObject<ShopBuyC2S>(a.Data.ToByteArray());
                                if (d == null) break;
                                var offer = BuildPvpShopOffer(a, d);
                                if (offer.CardIds.Length == 0 && offer.BuyCards.Length == 0) break;
                                _reportedShopSn.Add(a.Sn);
                                Emit(ShopOffered, offer);
                                if (offer.CardIds.Length > 0)
                                    Emit<long, IReadOnlyList<int>>(ShopCandidates, a.PlayerId, offer.CardIds);
                            }
                            break;

                        case 5215: // PVE 商店(Payload: PVEShopBuyC2S)
                            {
                                if (!wantShop || _reportedShopSn.Contains(a.Sn)) break;
                                var d = ByteBuf.ReadObject<PVEShopBuyC2S>(a.Data.ToByteArray());
                                if (d == null) break;
                                var offer = BuildPveShopOffer(a, d);
                                if (offer.CardIds.Length == 0 && offer.BuyCards.Length == 0) break;
                                _reportedShopSn.Add(a.Sn);
                                Emit(ShopOffered, offer);
                                if (offer.CardIds.Length > 0)
                                    Emit<long, IReadOnlyList<int>>(ShopCandidates, a.PlayerId, offer.CardIds);
                            }
                            break;

                        case 5211: // 筹码格(遗物)候选(Payload: SelectRelicC2S)
                            {
                                if (!wantRelic || _reportedRelicSn.Contains(a.Sn)) break;
                                var d = ByteBuf.ReadObject<SelectRelicC2S>(a.Data.ToByteArray());
                                if (d == null || d.Relics == null || d.Relics.Count == 0) break;
                                _reportedRelicSn.Add(a.Sn);

                                var ids = new int[d.Relics.Count];
                                d.Relics.CopyTo(ids, 0);
                                Emit(RelicOffered, new RelicOffer
                                {
                                    PlayerId = a.PlayerId,
                                    Sn = a.Sn,
                                    RelicIds = ids,
                                    IsReroll = d.IsReroll,
                                    Index = d.Idx,
                                    Lv = d.Lv,
                                    SupLv = d.SupLv
                                });
                                Emit<long, IReadOnlyList<int>>(RelicCandidates, a.PlayerId, ids);
                            }
                            break;
                    }
                }
                catch { }
            }
        }

        private static ShopOffer BuildPveShopOffer(party.model.Action a, PVEShopBuyC2S d)
        {
            return new ShopOffer
            {
                PlayerId = a.PlayerId,
                Sn = a.Sn,
                CardIds = CopyInts(d.Cards),
                BuyCards = CopyInts(d.BuyCards),
                AlreadyBought = CopyBools(d.Alreadys),
                Free = CopyBools(d.TalentSkillFreeCard),
                Gold = d.Gold,
                DisCountGold = d.DisCountGold,
                AssistPlayer = d.AssistPlayer,
                AssistGold = d.AssistGold,
                IsClose = d.IsClose,
                IsPve = true
            };
        }

        private static ShopOffer BuildPvpShopOffer(party.model.Action a, ShopBuyC2S d)
        {
            return new ShopOffer
            {
                PlayerId = a.PlayerId,
                Sn = a.Sn,
                CardIds = CopyInts(d.Cards),
                BuyCards = CopyInts(d.BuyCards),
                AlreadyBought = CopyBools(d.Alreadys),
                Free = Array.Empty<bool>(),
                Gold = d.Gold,
                FreeCard = d.FreeCard,
                FreeCardNum = d.FreeCardNum,
                IsPve = false
            };
        }

        private static ShopSettlement BuildPveSettlement(PVEShopBuyS2C m, Core.Net.RPCMsgManager net)
        {
            return new ShopSettlement
            {
                PlayerId = m.PlayerId,
                BuyCards = CopyInts(m.BuyCards),
                Cards = CopyInts(m.Cards),
                Already = CopyBools(m.Alreadys),
                IsClose = m.IsClose,
                AssistPlayer = m.AssistPlayer,
                IsPve = true,
                DownSn = DownSnReader.Read(net, GameCommands.PveShopBuy)
            };
        }

        private static ShopSettlement BuildPvpSettlement(ShopBuyS2C m, Core.Net.RPCMsgManager net)
        {
            return new ShopSettlement
            {
                PlayerId = m.PlayerId,
                BuyCards = CopyInts(m.BuyCards),
                Cards = CopyInts(m.Cards),
                Already = CopyBools(m.Alreadys),
                IsClose = false,
                AssistPlayer = 0,
                IsPve = false,
                DownSn = DownSnReader.Read(net, GameCommands.ShopBuy)
            };
        }

        /// <summary>复制 RepeatedField: 订阅者不能改到协议对象里的原集合。</summary>
        private static int[] CopyInts(Google.Protobuf.Collections.RepeatedField<int> src)
        {
            if (src == null || src.Count == 0) return Array.Empty<int>();
            var copy = new int[src.Count];
            src.CopyTo(copy, 0);
            return copy;
        }

        private static bool[] CopyBools(Google.Protobuf.Collections.RepeatedField<bool> src)
        {
            if (src == null || src.Count == 0) return Array.Empty<bool>();
            var copy = new bool[src.Count];
            src.CopyTo(copy, 0);
            return copy;
        }

        /// <summary>把一条 Action 原样广播给 <see cref="RawAction"/> 订阅者(无订阅者时零分配)。</summary>
        private static void EmitRawAction(party.model.Action a)
        {
            var handler = RawAction;
            if (handler == null) return;
            try
            {
                byte[] data = null;
                try
                {
                    if (a.Data != null && a.Data.Length > 0) data = a.Data.ToByteArray();
                }
                catch { }

                var evt = new RawActionEvent
                {
                    Id = a.Id,
                    Sn = a.Sn,
                    PlayerId = a.PlayerId,
                    Data = data
                };

                // 逐个订阅者 try/catch: 一个 mod 抛异常不能影响其它订阅者, 更不能影响游戏
                var list = handler.GetInvocationList();
                for (int i = 0; i < list.Length; i++)
                {
                    try { ((Action<RawActionEvent>)list[i])(evt); } catch { }
                }
            }
            catch { }
        }
    }
}

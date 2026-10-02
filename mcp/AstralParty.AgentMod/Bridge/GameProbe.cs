using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK;
using GameLogic;
using party.protocol;
using Tools;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 倒计时 / 棋盘 / 用牌窗口的探针。
    ///
    /// 全部走"编译期能拿到的类型 + 少量私有字段反射", 依据是反编译结论(见 docs/MCP-Agent桥接.md):
    ///   - <c>GameLogic.OperationTimer</c>(public static): <c>GetOperateTimer(sn)</c> → Timer.GetTimeRemaining()
    ///     (Timer 是 AOT 类型, 只能反射调用), 兜底读私有静态 <c>operationTime - downtime</c>(秒);
    ///   - <c>ActionLogic.CardSN</c> / <c>UsableCards</c>: 编译期成员, 直接读;
    ///   - 移动候选: <c>BattleLogic.GetSelfPlayerData().CharacterInst.standLand.CanSelectedLandId(fromLandId)</c>,
    ///     编译期成员, 直接读(比反射 MoveArrowManager 的箭头池更稳, 且唯一方向时也有结果)。
    ///
    /// **全部只在主线程调用**(反射 + Unity 对象), 绝不从网络线程进。
    /// </summary>
    internal static class GameProbe
    {
        private static bool _initAttempted;

        private static Type _operationTimerType;
        private static FieldInfo _operationTimeField;
        private static FieldInfo _downtimeField;
        private static FieldInfo _timerDictField;
        private static MethodInfo _getOperateTimerMethod;
        private static MethodInfo _cancelOperateTimerMethod;
        private static MethodInfo _timerGetRemainingMethod;

        private static void EnsureInit()
        {
            if (_initAttempted) return;
            _initAttempted = true;
            try
            {
                // OperationTimer 在热更主程序集里(和 party.protocol 同一个程序集)
                _operationTimerType = typeof(MoveC2S).Assembly.GetType("GameLogic.OperationTimer", false);
                if (_operationTimerType == null) return;

                const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _operationTimeField = _operationTimerType.GetField("operationTime", Static);
                _downtimeField = _operationTimerType.GetField("downtime", Static);
                // 反编译显示字段名是 timerDict(无下划线); MEMORY.md 里写的是 _timerDict —— 两个都试
                _timerDictField = _operationTimerType.GetField("timerDict", Static) ??
                                  _operationTimerType.GetField("_timerDict", Static);
                _getOperateTimerMethod = _operationTimerType.GetMethod("GetOperateTimer", Static);
                _cancelOperateTimerMethod = _operationTimerType.GetMethod("CancelOperatTimer", Static);
            }
            catch { }
        }

        /// <summary>探针是否可用(不可用时 RemainingMs 返回 -1, 不影响其他功能)。</summary>
        public static bool Available { get { EnsureInit(); return _operationTimerType != null; } }

        /// <summary>
        /// 读当前操作倒计时剩余毫秒(-1 = 读不到)。
        /// 优先级: GetOperateTimer(sn).GetTimeRemaining() → operationTime - downtime。
        /// </summary>
        public static long RemainingMs(long sn)
        {
            EnsureInit();
            if (_operationTimerType == null) return -1;

            // 1) 按 sn 取 Timer(AOT 类型 → 反射), 返回类型在元数据里只确认到"float 秒"
            if (sn != 0 && _getOperateTimerMethod != null)
            {
                try
                {
                    object timer = _getOperateTimerMethod.Invoke(null, new object[] { sn });
                    if (timer != null)
                    {
                        if (_timerGetRemainingMethod == null)
                            _timerGetRemainingMethod = timer.GetType().GetMethod("GetTimeRemaining", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_timerGetRemainingMethod != null)
                        {
                            object v = _timerGetRemainingMethod.Invoke(timer, null);
                            double sec = Convert.ToDouble(v);
                            if (!double.IsNaN(sec) && sec >= 0) return (long)(sec * 1000.0);
                        }
                    }
                }
                catch { }
            }

            // 2) 兜底: 总时长 - 已用时(两个 float 静态字段, 单位秒)
            try
            {
                if (_operationTimeField == null || _downtimeField == null) return -1;
                object total = _operationTimeField.GetValue(null);
                object elapsed = _downtimeField.GetValue(null);
                if (total == null || elapsed == null) return -1;
                double t = Convert.ToDouble(total);
                double d = Convert.ToDouble(elapsed);
                double left = t - d;
                if (double.IsNaN(left)) return -1;
                if (left < 0) left = 0;
                return (long)(left * 1000.0);
            }
            catch { return -1; }
        }

        /// <summary>当前所有正在倒计时的操作 sn(= 服务器等我应答的集合)。读不到返回空数组。</summary>
        public static long[] ActiveOperationSns()
        {
            EnsureInit();
            if (_timerDictField == null) return Array.Empty<long>();
            try
            {
                object dict = _timerDictField.GetValue(null);
                var result = new List<long>();
                var enumerable = dict as IEnumerable;
                if (enumerable == null) return Array.Empty<long>();
                foreach (object key in enumerable)
                {
                    if (key is long l) result.Add(l);
                    else
                    {
                        try { result.Add(Convert.ToInt64(key)); } catch { }
                    }
                }
                return result.ToArray();
            }
            catch { return Array.Empty<long>(); }
        }

        /// <summary>
        /// 取消某个 sn 的倒计时。**必须在动作发送成功之后调用**:
        /// 游戏在每个窗口都挂了"超时自动代打"回调(选第一项/空购买离店),
        /// 我们已经应答了还留着计时器, 到点它会再发一次请求。
        /// </summary>
        public static void CancelOperationTimer(long sn)
        {
            EnsureInit();
            if (sn == 0 || _cancelOperateTimerMethod == null) return;
            try { _cancelOperateTimerMethod.Invoke(null, new object[] { sn }); } catch { }
        }

        // ============================== 用牌窗口 ==============================

        /// <summary>读 ActionLogic 的待响应 sn 与可用牌列表。取不到返回 false。</summary>
        public static bool TryReadCardWindow(out long cardSn, out int[] usableCards, out bool notMove)
        {
            cardSn = 0;
            usableCards = null;
            notMove = false;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var action = gm != null ? gm.action : null;
                if (action == null) return false;

                cardSn = action.CardSN;
                notMove = action.NotMove;
                var usable = action.UsableCards;
                if (usable != null && usable.Count > 0)
                {
                    var list = new List<int>();
                    var e = ((IEnumerable)usable).GetEnumerator();
                    while (e.MoveNext())
                    {
                        try { list.Add(Convert.ToInt32(e.Current)); } catch { }
                    }
                    usableCards = list.ToArray();
                }
                return true;
            }
            catch { return false; }
        }

        // ============================== 移动候选 ==============================

        /// <summary>
        /// 算"我能走到的地块"。只有轮到我走时才有意义(调用方负责判断窗口)。
        /// 用 Character.standLand.CanSelectedLandId(fromLandId) —— 与客户端建箭头用的是同一个方法。
        /// </summary>
        public static int[] SelfMoveTargets()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                if (battle == null) return null;

                var selfData = battle.GetSelfPlayerData();
                var ch = selfData != null ? selfData.CharacterInst : null;
                if (ch == null) return null;

                var land = ch.standLand;
                if (land == null) return null;

                var lands = land.CanSelectedLandId(ch.fromLandId);
                if (lands == null) return null;

                var result = new List<int>();
                var e = ((IEnumerable)lands).GetEnumerator();
                while (e.MoveNext())
                {
                    try
                    {
                        int id = Convert.ToInt32(e.Current);
                        if (id != 0) result.Add(id);
                    }
                    catch { }
                }
                return result.ToArray();
            }
            catch { return null; }
        }

        /// <summary>
        /// 战斗出牌窗口(5035)的候选牌 —— 复刻客户端 <c>UI.FightWindow.GetVailCard()</c>:
        /// 取我方手牌里 <c>Config.EffectType</c> 等于"我方角色对应的效果类型"的那些牌
        /// (我是攻击方 → <c>EffectType.Attack</c>, 我是防守方 → <c>EffectType.Defense</c>),
        /// 并给出每张牌的战斗消耗与"我方剩余战斗点数"(<c>fightData.attackerInfo.Cost</c> /
        /// <c>defenderInfo.Cost</c>, 对应 UI 的 <c>AttackerResidueCost</c>/<c>DefenderResidueCost</c>)。
        ///
        /// 候选 id 用**手牌 Guid**(战斗用牌发的是 Guid, 不是卡牌配置 CardId)。
        /// 只在轮到我出牌时调用。
        /// </summary>
        public static bool TrySelfFightCards(out int[] cardUids, out int[] costs, out int residueCost, out bool isAttacker)
        {
            cardUids = null;
            costs = null;
            residueCost = -1;
            isAttacker = false;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var fight = gm != null ? gm.fight : null;
                if (fight == null) return false;

                var attacker = fight.attackData;
                var defender = fight.defendData;
                if (attacker == null || defender == null) return false;

                long self = 0;
                try { self = gm.account.GetPlayerID(); } catch { }
                if (self == 0) return false;

                bool iAmAttacker = attacker.PlayerId == self;
                bool iAmDefender = defender.PlayerId == self;
                if (!iAmAttacker && !iAmDefender) return false;
                isAttacker = iAmAttacker;
                residueCost = iAmAttacker ? attacker.Cost : defender.Cost;

                var hand = Players.MyHandCards();
                if (hand == null) return false;

                // 全局枚举 EffectType(见 CardInfoConfigure.EffectType): 攻击方只能用 Attack 类牌, 防守方只能用 Defense 类牌
                EffectType want = iAmAttacker ? EffectType.Attack : EffectType.Defense;

                var uids = new List<int>();
                var cs = new List<int>();
                for (int i = 0; i < hand.Count; i++)
                {
                    var hc = hand[i];
                    if (hc == null) continue;
                    try
                    {
                        var cfg = hc.Config;
                        if (cfg == null) continue;
                        if (cfg.EffectType != want) continue;
                    }
                    catch { continue; }
                    if (hc.Guid == 0) continue;
                    uids.Add(hc.Guid);
                    cs.Add(SafeCardCost(gm, self, hc));
                }
                cardUids = uids.ToArray();
                costs = cs.ToArray();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 追击窗口(5213)的候选怪物 —— 与客户端 <c>LandLogic.GetVailPursuitMonster()</c> 完全同口径:
        /// <c>CharacterType.Monster &amp;&amp; !Property.NotSelect &amp;&amp; HP &gt; 0 &amp;&amp; CharacterInst != null
        /// &amp;&amp; standLand.LandType != LandType.Hospital &amp;&amp; 与我不同队伍</c>。
        /// 候选就是这些单位(怪物也是玩家)的 playerId, 应答时填进 <c>MonsterPursuitC2S.SelectId</c>。
        /// </summary>
        /// <remarks>
        /// 只用 <c>var</c> 不写死类型名: <c>Character</c>/<c>UnitLand</c> 在热更程序集里, 类型名可能带命名空间,
        /// 而成员访问不受影响(程序集是直接引用的)。
        /// </remarks>
        public static bool TrySelfPursuitMonsters(out long[] monsterIds)
        {
            monsterIds = null;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                if (battle == null) return false;

                var self = battle.GetSelfPlayerData();
                if (self == null) return false;

                var list = battle.PlayerDatas;
                if (list == null) return false;

                var ids = new List<long>();
                for (int i = 0; i < list.Count; i++)
                {
                    var pd = list[i];
                    if (pd == null || pd.player == null) continue;
                    try
                    {
                        if (pd.characterType != CharacterType.Monster) continue;
                        if (pd.Property != null && pd.Property.NotSelect != null && pd.Property.NotSelect.Value) continue;
                        if (pd.Property == null || pd.Property.HP == null || pd.Property.HP.Value <= 0) continue;
                        var inst = pd.CharacterInst;
                        if (inst == null) continue;
                        var land = inst.standLand;
                        if (land == null) continue;
                        if (land.LandType == LandType.Hospital) continue;
                        if (self.player != null && self.player.TeamId == pd.player.TeamId) continue;
                    }
                    catch { continue; }
                    if (pd.player.Id != 0) ids.Add(pd.player.Id);
                }
                monsterIds = ids.ToArray();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 炮台选目标(5063)的候选英雄 —— 与客户端 <c>LandBatteryWindow.GetBatteryTargets</c> 完全同口径:
        /// 遍历 <c>battle.PlayerDatas</c>, 只收 <c>characterType == Hero</c> 且 <paramref name="isCandidate"/>(id) 为 true 的玩家,
        /// 顺序就是客户端的显示顺序(客户端用 <c>canTargetIds.TryGetValue(id, out v) &amp;&amp; v</c>)。
        /// 读不到战斗数据返回 false(此时调用方不要伪装成"没有候选")。
        /// </summary>
        public static bool TryBatteryTargets(Func<long, bool> isCandidate, out long[] playerIds)
        {
            playerIds = null;
            try
            {
                if (isCandidate == null) return false;
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                if (battle == null) return false;
                var list = battle.PlayerDatas;
                if (list == null) return false;

                var ids = new List<long>();
                for (int i = 0; i < list.Count; i++)
                {
                    var pd = list[i];
                    if (pd == null || pd.player == null) continue;
                    try
                    {
                        if (pd.characterType != CharacterType.Hero) continue;
                        if (!isCandidate(pd.player.Id)) continue;
                    }
                    catch { continue; }
                    if (pd.player.Id != 0) ids.Add(pd.player.Id);
                }
                playerIds = ids.ToArray();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 赌场(5083 掷骰窗口)的"我能不能参与 + 按钮可不可点" —— 与客户端 <c>DealLand_GambleDice</c> 同口径:
        /// 从 <c>room.curRoomInfo.Hall.Roles</c> 里找我的 role(找到 = <c>_enableJoinGamble</c>), 再看
        /// <c>IsDie</c>/<c>GoldLack</c>(客户端据此把按钮置灰)。
        /// 读不到房间/大厅返回 false —— 此时客户端也不会有任何上行(<c>_enableJoinGamble</c> 保持默认 false),
        /// 所以桥接同样不开窗。
        /// </summary>
        public static bool TrySelfGambleHall(out bool inHall, out bool canAct)
        {
            inHall = false;
            canAct = false;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null) return false;
                var room = gm.room;
                var info = room != null ? room.curRoomInfo : null;
                var hall = info != null ? info.Hall : null;
                if (hall == null || hall.Roles == null) return false;
                var battle = gm.battle;
                var self = battle != null ? battle.GetSelfPlayerData() : null;
                if (self == null || self.player == null) return false;
                long selfId = self.player.Id;
                for (int i = 0; i < hall.Roles.Count; i++)
                {
                    var r = hall.Roles[i];
                    if (r == null || r.PlayerId != selfId) continue;
                    inHall = true;
                    canAct = !(r.IsDie || r.GoldLack);
                    return true;
                }
                return true;   // 读到了大厅, 只是我不在里面(inHall 保持 false)
            }
            catch { return false; }
        }

        /// <summary>
        /// 抽奖窗口(5041)的"号码上限 + 我已经占了的号码" —— 与客户端 <c>LandLotteryWindow</c> 同口径:
        /// 上限 = <c>StaticGlobalData.GAME_LAND_LOTTERY_NUMB_LIMIT</c>(该类型在全局命名空间),
        /// 已占 = <c>player.Hero.Lotterys</c> 里值为 true 的号码(客户端会把它们置为不可选)。
        /// 读不到返回 false(此时不要开窗)。
        /// </summary>
        public static bool TrySelfLottery(out int numbLimit, out int[] ownedNumbers)
        {
            numbLimit = 0;
            ownedNumbers = null;
            try
            {
                int limit = StaticGlobalData.GAME_LAND_LOTTERY_NUMB_LIMIT;
                if (limit <= 0) return false;
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                var self = battle != null ? battle.GetSelfPlayerData() : null;
                if (self == null || self.player == null || self.player.Hero == null) return false;
                var map = self.player.Hero.Lotterys;

                var owned = new List<int>();
                if (map != null)
                {
                    for (int n = 1; n <= limit; n++)
                    {
                        bool v;
                        if (map.TryGetValue(n, out v) && v) owned.Add(n);
                    }
                }
                numbLimit = limit;
                ownedNumbers = owned.ToArray();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 追击地块窗口(5033)的候选敌方英雄 —— 与客户端 <c>LandPursuitWindow.InitAvailablePlayer</c> 同口径:
        /// <c>characterType == Hero &amp;&amp; 不是我 &amp;&amp; 不同队 &amp;&amp; !Property.NotSelect</c>,
        /// 再按"还能不能打"过滤(血量&gt;0 且不在医院地块 —— 客户端会把这类行置灰、确定键也点不亮),
        /// 与 5213 的候选人过滤同一套口径。
        /// 候选**不在协议里**(5033 的 Data 客户端从不解码), 所以只能本地算。读不到返回 false。
        /// </summary>
        public static bool TrySelfPursuitPlayers(out long[] playerIds)
        {
            playerIds = null;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                if (battle == null) return false;
                var self = battle.GetSelfPlayerData();
                if (self == null || self.player == null) return false;
                var list = battle.PlayerDatas;
                if (list == null) return false;

                var ids = new List<long>();
                for (int i = 0; i < list.Count; i++)
                {
                    var pd = list[i];
                    if (pd == null || pd.player == null) continue;
                    try
                    {
                        if (pd.characterType != CharacterType.Hero) continue;
                        if (pd.player.Id == self.player.Id) continue;
                        if (pd.player.TeamId == self.player.TeamId) continue;
                        if (pd.Property != null && pd.Property.NotSelect != null && pd.Property.NotSelect.Value) continue;
                        if (pd.Property == null || pd.Property.HP == null || pd.Property.HP.Value <= 0) continue;
                        var inst = pd.CharacterInst;
                        if (inst != null)
                        {
                            var land = inst.standLand;
                            if (land != null && land.LandType == LandType.Hospital) continue;
                        }
                    }
                    catch { continue; }
                    if (pd.player.Id != 0) ids.Add(pd.player.Id);
                }
                playerIds = ids.ToArray();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 助力投票(5309)的三路候选 —— 与客户端 <c>AssistVoteLogic.LeftMonster/RightMonster/CenterMonster</c> 同口径:
        /// 在 <c>StaticConfigure.PVEMission.Votes</c> 里找 <c>MapId</c> 等于当前房间 MapId 的那一项, 再取
        /// 下标 +0(右) / +1(左) / +2(中)(客户端用 <c>GetSafeByIndex</c>, 越界给 null)。
        /// 三个出参里 <c>0</c> = 这张图没有这一路(82013 只有右/左, 82015/S7 有右/左/中)。读不到返回 false。
        /// </summary>
        public static bool TrySelfAssistVote(out int rightId, out int leftId, out int centerId)
        {
            rightId = 0;
            leftId = 0;
            centerId = 0;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm != null ? gm.room : null;
                var info = room != null ? room.curRoomInfo : null;
                if (info == null) return false;

                var pve = StaticConfigure.PVEMission;
                var votes = pve != null ? pve.Votes : null;
                if (votes == null || votes.Count == 0) return false;

                int mapId = info.MapId;
                int hit = -1;
                for (int i = 0; i < votes.Count; i++)
                {
                    var v = votes[i];
                    if (v != null && v.MapId == mapId) { hit = i; break; }
                }
                if (hit < 0) return false;

                rightId = VoteIdAt(votes, hit + 0);
                leftId = VoteIdAt(votes, hit + 1);
                centerId = VoteIdAt(votes, hit + 2);
                return true;
            }
            catch { return false; }
        }

        /// <summary>取 <c>Votes[index].Id</c>(越界返回 0)。收 <see cref="IList"/> 是为了不必引 protobuf 的集合类型。</summary>
        private static int VoteIdAt(IList votes, int index)
        {
            try
            {
                if (votes == null || index < 0 || index >= votes.Count) return 0;
                var v = votes[index] as PVEMissionVoteConfigure;
                return v != null ? v.Id : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 我(或指定玩家)当前站在什么地块上 —— 5077 窗口的全部"信息"都来自本地玩家状态
        /// (那条动作的 Data 恒为 0 字节, 见 decomp/四窗口契约.md)。
        /// 返回 "born" / "fillingStation" / "other"; 读不到返回 null。
        /// </summary>
        public static string SelfStandLand()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                if (battle == null) return null;
                var self = battle.GetSelfPlayerData();
                if (self == null || self.CharacterInst == null) return null;
                var land = self.CharacterInst.standLand;
                if (land == null) return null;
                var t = land.LandType;
                if (t == LandType.Born) return "born";
                if (t == LandType.FillingStation) return "fillingStation";
                return "other";
            }
            catch { return null; }
        }

        /// <summary>单张战斗牌的消耗: 优先 per-card 逻辑(<c>cardActions[cardId].GetCostValue</c>), 退回手牌自带 BattleCost。</summary>
        private static int SafeCardCost(GameLogicManager gm, long self, HandCardData hc)
        {
            try
            {
                var card = gm != null ? gm.card : null;
                var actions = card != null ? card.cardActions : null;
                if (actions != null && actions.TryGetValue(hc.CardId, out var act) && act != null)
                    return act.GetCostValue(self, hc);
            }
            catch { }
            try { return hc.BattleCost; } catch { return 0; }
        }
    }
}

using System;
using System.Collections.Generic;
using AstralParty.Agent;

namespace AstralParty.AgentMod.Bridge
{
    internal sealed partial class PendingTracker
    {
        // ============================== 事件入口(可能来自网络线程) ==============================

        public void OnRelicCandidates(long playerId, IReadOnlyList<int> relicIds, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.SelectRelic, PlayerId = playerId, Ids = ToArray(relicIds), Sn = sn, SinceMs = nowMs });
        }

        public void OnRewardCandidates(long playerId, IReadOnlyList<int> cardIds, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.RewardCard, PlayerId = playerId, Ids = ToArray(cardIds), Sn = sn, SinceMs = nowMs });
        }

        public void OnShopCandidates(long playerId, int[] ids, int[] prices, bool[] soldOut, bool[] free, bool pve, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.Shop,
                PlayerId = playerId,
                Ids = ids,
                Prices = prices,
                SoldOut = soldOut,
                Free = free,
                PveShop = pve,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>筹码地块购买 offer(5249)。</summary>
        public void OnBuyRelicOffer(long playerId, long sn, int relicGold, int divinationGold, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.BuyRelic,
                PlayerId = playerId,
                Sn = sn,
                RelicGold = relicGold,
                DivinationGold = divinationGold,
                SinceMs = nowMs
            });
        }

        /// <summary>移动动作(5027): 服务器只通知"该你走", 候选地块稍后由主线程算出来。</summary>
        public void OnMoveAction(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.Move, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        public void OnRelicSelected(long playerId, int relicId, long nowMs) { ClearIf(AgentPendingKind.SelectRelic, playerId); }
        public void OnRewardSelected(long playerId, int cardId, long nowMs) { ClearIf(AgentPendingKind.RewardCard, playerId); }

        /// <summary>移动回执(S2C Move): 窗口关闭。</summary>
        public void OnMoved(long playerId, long nowMs)
        {
            ClearIf(AgentPendingKind.Move, playerId);
            lock (_lock) { _moveLands = null; }
        }

        /// <summary>商店购买回执 / 进店未买: 窗口关闭。</summary>
        public void OnShopDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.Shop, playerId); }

        /// <summary>筹码地块回执(5250): 窗口关闭。</summary>
        public void OnBuyRelicDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.BuyRelic, playerId); }

        // ---------- 战斗攻击骰窗口(5037 / 回执 5038) ----------

        /// <summary>
        /// 战斗攻击骰窗口(5037 = ReadyFightThrowDice)。
        ///
        /// 依据(反编译 FightLogic.ReadyFightThrowDice / RequestBattleThrowDiceC2S):
        /// 窗口归属 = action.PlayerId, 要回传的 sn = action.Sn, 应答消息 = BattleThrowDiceC2S。
        /// 这条动作**没有可解码的业务负载**(客户端自己也不解 Data), 所以判"是不是新窗口"只能靠 sn —— 交给
        /// <see cref="SetWindow"/> 的"已应答 sn"挡板处理(服务器会把我自己的决定回播成同 sn 的动作)。
        /// </summary>
        public void OnBattleDiceOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.BattleDice, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>战斗掷骰回执(5038 = BattleThrowDiceS2C): 窗口关闭。</summary>
        public void OnBattleDiceDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.BattleDice, playerId); }

        // ---------- 事件选择窗口(5317 / 回执 5318) ----------

        /// <summary>
        /// 事件选择候选(5317)。候选 = <c>SelectEventC2S.Events</c>, sn = action.Sn。
        ///
        /// 依据(反编译 UI.LandEventWindow.ShowSkill10202): 客户端把服务器那条消息**原样**改
        /// <c>Idx</c> 与 <c>Info.Sn</c> 后发回, 所以候选 id 列表必须留着(应答时要回传)。
        /// 该窗口超时会被服务器代选第 0 项。
        /// </summary>
        public void OnEventCandidates(long playerId, IReadOnlyList<int> eventIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.SelectEvent,
                PlayerId = playerId,
                Ids = ToArray(eventIds),
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>事件选择回执(5318 = SelectEventS2C): 窗口关闭。</summary>
        public void OnEventSelected(long playerId, long nowMs) { ClearIf(AgentPendingKind.SelectEvent, playerId); }

        // ---------- 战斗询问窗口(5047 / 回执 5048) ----------

        /// <summary>
        /// "要不要打这一场"(5047 = <c>FightLogic.AskFight</c>)。offer = <c>AskBattleC2S</c>,
        /// 其中 <c>AskPlayerId</c> = 挑战者, <c>FightBack</c> = 服务器已经知道答案(客户端会自己以
        /// <c>IsBattle=true</c> 自动应答, 真人玩家根本没有选择机会) —— **FightBack=true 时不要开这个窗口**,
        /// 否则桥接会跟客户端抢答同一条 sn。
        ///
        /// 依据(反编译 <c>UI.FightWindow.OpenChallengeWin/SureLaunch/RequestClosePKWin</c>):
        /// 归属 = <c>action.PlayerId</c>(只对本人注册超时); 超时回调点的是 <c>btn_Leave</c> →
        /// <c>IsBattle=false</c>, 即**不答 = 不打**。
        /// </summary>
        public void OnAskFightOffer(long playerId, long askPlayerId, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.AskFight,
                PlayerId = playerId,
                AskPlayerId = askPlayerId,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>战斗询问回执(5048 = AskBattleS2C): 窗口关闭。</summary>
        public void OnAskFightDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.AskFight, playerId); }

        // ---------- 战斗用牌窗口(5035 / 回执 5036) ----------

        /// <summary>
        /// 战斗准备阶段轮到我出牌(5035 = <c>FightLogic.ReadyFightUseCard</c>)。
        ///
        /// 这条动作**没有可解码的负载**(反编译 <c>UI.FightWindow.RefreshPKCard</c> 只用了 action.PlayerId 与 action.Sn)。
        /// 候选牌由客户端本地算: <c>GetVailCard()</c> = 我方手牌里 <c>Config.EffectType</c> 匹配我方角色的那些
        /// (我是攻击方 → <c>EffectType.Attack</c>, 我是防守方 → <c>EffectType.Defense</c>),
        /// 再由 <c>RefreshCardUsability</c> 按剩余战斗点数把买不起的牌置灰 —— 这两步由
        /// <see cref="SetFightCardCandidates"/> 从主线程喂进来。
        ///
        /// 超时回调点的是 <c>btn_FinishPkCard</c> → <c>CardUid = 0</c>, 即**不答 = 不出牌**。
        /// </summary>
        public void OnFightCardOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.FightCard, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>战斗用牌回执(5036 = BattleUseCardS2C): 窗口关闭。</summary>
        public void OnFightCardDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.FightCard, playerId); }

        // ---------- 战斗闪避窗口(5039 / 回执 5040) ----------

        /// <summary>
        /// 防守方选"闪避 / 硬吃"(5039 = <c>FightLogic.ReadyFightChoice</c>)。offer = <c>BattleChoiceC2S</c>,
        /// 只需要读它的 <c>NoDodge</c>。
        ///
        /// 依据(反编译 <c>UI.FightWindow.RefreshDefendReadyChoice/ChooseActive</c>):
        /// 归属 = <c>action.PlayerId</c>; <c>NoDodge=true</c> 时客户端**直接拒绝闪避请求**(只弹个提示、不发包),
        /// 所以这时 agent 只能回 <c>dodge=false</c>; 超时回调点的是 <c>btn_Defend</c> → <c>Dodge=false</c>,
        /// 即**不答 = 不闪避**。
        /// </summary>
        public void OnFightChoiceOffer(long playerId, bool noDodge, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.FightChoice,
                PlayerId = playerId,
                NoDodge = noDodge,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>闪避选择回执(5040 = BattleChoiceS2C): 窗口关闭。</summary>
        public void OnFightChoiceDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.FightChoice, playerId); }

        // ---------- 加油站/出生点窗口(5077 / 回执 5078) ----------

        /// <summary>
        /// 服务器问"走到这里要停下还是继续走"(5077 = <c>UI.LandFillingStationWindow.DealLand_StopOrContinue</c>)。
        ///
        /// 依据(反编译 + decomp/四窗口契约.md): 这条动作**完全没有业务负载** —— 客户端从来不
        /// <c>ReadObject</c>, 3 局真实回放里 <c>Data</c> 长度恒为 0; 窗口要显示的东西全在本地玩家状态里
        /// (<c>standLand.LandType</c>、星币、等级…), 由 <see cref="SetStandLand"/> 从主线程喂进来。
        /// 只有本人弹双按钮窗口; 超时回调点的是"继续走" → **不答 = 继续走**(<c>Stop=false</c>)。
        /// </summary>
        public void OnStopOrContinueOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.StopOrContinue, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>加油站/出生点回执(5078 = StopOrContinueS2C): 窗口关闭。</summary>
        public void OnStopOrContinueDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.StopOrContinue, playerId); }

        // ---------- 复活队友窗口(5233 / 回执 5234) ----------

        /// <summary>
        /// 服务器问"要不要复活倒下的队友"(5233 = <c>LandLogic.DealAskReviveTeammate</c>)。
        ///
        /// 依据(反编译): 这条动作**没有业务负载** —— 窗口 <c>ShowAskReviveTeammate(action)</c> 只读
        /// <c>action.Sn</c> 与 <c>action.PlayerId</c>, 从不 <c>ReadObject</c>; 该显示什么(谁倒下了、
        /// 要花多少星币)全在本地玩家状态里。窗口只在本人这边弹出, 其他人只看到"思考中"(11000)。
        /// 两个按钮: <c>btn_Stop</c> → <c>IsRevive=true</c>、<c>btn_Continue</c> → <c>IsRevive=false</c>;
        /// 超时回调点的是 <c>btn_Continue</c> → **不答 = 不复活**(<c>IsRevive=false</c>)。
        /// </summary>
        public void OnReviveTeammateOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.ReviveTeammate, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>复活队友回执(5234 = AskReviveTeammateS2C): 窗口关闭。</summary>
        public void OnReviveTeammateDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.ReviveTeammate, playerId); }

        // ---------- 机制选择窗口(5259 / 回执 5260) ----------

        /// <summary>
        /// 服务器问"要不要启动这个地块机制"(5259 = <c>LandLogic.DealAskSelectMechanism</c>)。
        ///
        /// 依据(反编译): **没有业务负载** —— 窗口 <c>ShowSelectMechanism(action)</c> 只读 <c>action.Sn</c>。
        /// <c>btn_Stop</c>("启动") → <c>Select=true</c>、<c>btn_Continue</c> → <c>Select=false</c>;
        /// 超时回调点的是 <c>btn_Continue</c> → **不答 = 不启动**(<c>Select=false</c>)。
        /// </summary>
        public void OnSelectMechanismOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.SelectMechanism, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>机制选择回执(5260 = SelectMechanismS2C): 窗口关闭。</summary>
        public void OnSelectMechanismDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.SelectMechanism, playerId); }

        // ---------- 医院窗口(5093 / 回执 5094) ----------

        /// <summary>
        /// 服务器问"要不要接受医院检查"(5093 = <c>UI.LandHospitalWindow.DealLand_TriggerHospital</c>)。
        ///
        /// 依据(反编译): **没有业务负载**(窗口只读 <c>_action.Sn</c>)。窗口里两个按钮, 但只有
        /// <c>btn_check</c> 会发包(<c>RequestTriggerHospitalC2S(sn)</c> → <c>TriggerHospitalC2S{Info}</c>),
        /// <c>btn_noSick</c> 只切本地视图、不上行。倒计时结束点的是 <c>btn_check</c> →
        /// **超时同样发"检查"**, 所以这条窗口没有"拒绝"这个语义。
        /// </summary>
        public void OnHospitalOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.HospitalCheck, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>医院回执(5094 = TriggerHospitalS2C): 窗口关闭。</summary>
        public void OnHospitalDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.HospitalCheck, playerId); }

        // ---------- 怪物追击窗口(5213 / 回执 5214) ----------

        /// <summary>
        /// 服务器问"要不要追击怪物、追哪一只"(5213 = <c>LandLogic.DealMonsterPursuit</c>)。
        ///
        /// 依据(反编译): 候选怪物**不在协议里**(<c>Data</c> 恒为 0 字节), 由客户端本地按
        /// <c>CharacterType.Monster &amp;&amp; !NotSelect &amp;&amp; HP&gt;0 &amp;&amp; 非医院地块 &amp;&amp; 不同队伍</c> 过滤 —— 桥接在
        /// <see cref="SetPursuitMonsters"/> 里复刻同一口径(来自 <c>GameProbe.TrySelfPursuitMonsters</c>)。
        /// 超时回调点的是"不追击" → **不答 = 不追击**(<c>SelectId=0</c>)。
        /// </summary>
        public void OnPursueMonsterOffer(long playerId, long sn, long nowMs)
        {
            SetWindow(new Window { Kind = AgentPendingKind.PursueMonster, PlayerId = playerId, Sn = sn, SinceMs = nowMs });
        }

        /// <summary>怪物追击回执(5214 = MonsterPursuitS2C): 窗口关闭。</summary>
        public void OnPursueMonsterDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.PursueMonster, playerId); }

        // ---------- 商人买卡窗口(5323 / 回执 5324) ----------

        /// <summary>
        /// 服务器问"要不要花 N 星币买下商人这张卡"(5323 = <c>LandLogic.DealAskVendorBuyCard</c>)。
        /// offer = <c>VendorBuyCardC2S{CardId, Gold}</c>(**没有 Info 字段**, 所以"Info.Sn==0"是用来区分
        /// offer 与"我的答案被回播"的依据)。客户端在星币不足时只弹提示、不发包。
        /// 超时回调点的是"取消" → **不答 = 不买**(<c>IsBuy=false</c>)。
        /// </summary>
        public void OnVendorCardOffer(long playerId, long cardId, int gold, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.VendorCard,
                PlayerId = playerId,
                VendorCardId = cardId,
                VendorPrice = gold,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>商人买卡回执(5324 = VendorBuyCardS2C): 窗口关闭。</summary>
        public void OnVendorCardDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.VendorCard, playerId); }

        // ---------- 控制移动卡选点窗口(5067 / 回执 5068) ----------

        /// <summary>
        /// 服务器问"这张控制移动卡要用几点移动力"(5067 = <c>CardWindow.RefreshCardInfo_ControlMoveCard</c>)。
        /// offer = <c>ThrowDiceResultC2S{MaxPoint}</c>(没有 Info), 应答 <c>Point ∈ 1..MaxPoint</c>。
        /// 只有本人开窗(<c>ActionListener</c> 里先判 <c>IsSelf</c>); 超时会把点数兜成 <c>1</c> → **不答 = 1 点**。
        /// </summary>
        public void OnSelectPointOffer(long playerId, int maxPoint, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.SelectPoint,
                PlayerId = playerId,
                MaxPoint = maxPoint,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>选点回执(5068 = ThrowDiceResultS2C): 窗口关闭。</summary>
        public void OnSelectPointDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.SelectPoint, playerId); }

        // ---------- 炮台选目标窗口(5063 / 回执 5064) ----------

        /// <summary>
        /// 服务器问"炮台要打哪几个英雄"(5063 = <c>UI.LandBatteryWindow.DealLand_LandChoiceTarget</c>, 仅 <c>LandType==11</c>)。
        /// offer 与答案**是同一个消息类** <c>LandChoiceTargetC2S</c>: offer 有 <c>LandType</c>/<c>TargetNum</c>/<c>CanTargetIds</c>,
        /// 答案是 <c>Info.Sn</c> + <c>TargetIds</c>(或 <c>Exit=true</c>) —— 所以用 <c>Info.Sn</c> 是否为 0 区分。
        /// 候选英雄由本地按 <c>characterType==Hero &amp;&amp; CanTargetIds[id]</c> 过滤(见 <c>GameProbe.TryBatteryTargets</c>);
        /// <paramref name="candidateIds"/> = null 表示"没算出来"(不要伪装成"没有目标")。
        /// 超时回调点的是"离开" → **不答 = 离开**(<c>Exit=true</c>)。
        /// </summary>
        public void OnBatteryOffer(long playerId, int targetNum, long[] candidateIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.BatteryTarget,
                PlayerId = playerId,
                TargetNum = targetNum,
                TargetIds = candidateIds,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>炮台选目标回执(5064 = LandChoiceTargetS2C): 窗口关闭。</summary>
        public void OnBatteryDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.BatteryTarget, playerId); }

        // ---------- 占卜窗口(5069 / 回执 5070) ----------

        /// <summary>
        /// 服务器问"占卜翻哪一张"(5069 = <c>UI.LandDivinationWindow.DealLand_Divination</c>)。
        /// offer = <c>TriggerDivinationC2S{CanChoiceIds}</c>(恰好两张; 同一消息类的答案则是 <c>Info</c> + <c>Id</c>,
        /// 靠 <c>Info.Sn==0</c> 区分, 同 5323/5067/5063)。只有本人能点, 其他人只看到"思考中"。
        /// 超时回调点的是 <c>btn_Divination_1</c> → **不答 = 选第 1 张**(<c>CanChoiceIds[0]</c>)。
        /// </summary>
        public void OnDivinationOffer(long playerId, IReadOnlyList<int> divinationIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.Divination,
                PlayerId = playerId,
                Ids = ToArray(divinationIds),
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>占卜回执(5070 = TriggerDivinationS2C): 窗口关闭。</summary>
        public void OnDivinationDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.Divination, playerId); }

        // ---------- 赌场窗口(5081 押注 / 5083 掷骰; 回执 5082/5084, 状态 1022) ----------

        /// <summary>
        /// 服务器问"赌场押奇数还是偶数"(5081 = <c>UI.LandGambleWindow.DealLand_Gamble</c>)。
        /// offer = <c>StartGambleC2S{Hall, IsExec}</c>(<c>IsExec</c> = 我能不能参与), 答案是
        /// <c>Info</c> + <c>IsExec</c> + <c>GuessCode</c> —— 靠 <c>Info.Sn</c> 区分(同 5323/5067/5063/5069)。
        /// 倒计时点的是 <c>btn_odd</c> → **不答 = 押奇数**(<c>GuessCode=1</c>)。
        /// 客户端在 <c>IsDie || GoldLack</c> 时把两个按钮都置灰(<c>touchable=false</c>), 真人点不动 ——
        /// 桥接记下 <paramref name="canAct"/>=false 并拒答(客户端自己的超时仍会押奇数)。
        /// </summary>
        public void OnGambleGuessOffer(long playerId, long sn, bool enableJoin, bool canAct, int betGold, long nowMs)
        {
            if (!enableJoin) return;   // 客户端 OnClick* 第一件事就是判 _enableJoinGamble: 不参与就没有任何上行
            SetWindow(new Window
            {
                Kind = AgentPendingKind.GambleGuess,
                PlayerId = playerId,
                Sn = sn,
                EnableJoin = enableJoin,
                CanAct = canAct,
                BetGold = betGold,
                SinceMs = nowMs
            });
        }

        /// <summary>
        /// 赌场掷骰窗口(5083 = <c>LandGambleWindow.DealLand_GambleDice</c>): **唯一合法上行就是掷骰**, 没有可选参数;
        /// 倒计时点的也是 <c>btn_Dice</c> → "不答"与"答"在服务器看来一样(同 5093 医院)。
        /// <paramref name="canAct"/>=false 时客户端把按钮置灰, 桥接拒答。
        /// </summary>
        public void OnGambleDiceOffer(long playerId, long sn, bool canAct, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.GambleDice,
                PlayerId = playerId,
                Sn = sn,
                CanAct = canAct,
                SinceMs = nowMs
            });
        }

        /// <summary>押注回执(5082 = StartGambleS2C, **该消息没有任何字段**): 关窗。</summary>
        public void OnGambleGuessDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.GambleGuess, playerId); }

        /// <summary>掷骰回执(5084 = GambleThrowDicS2C{PlayerId, Point}): 关窗。</summary>
        public void OnGambleDiceDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.GambleDice, playerId); }

        /// <summary>
        /// 赌场状态变化(1022 = <c>GambleChangeS2C{Hall}</c>, 由观察者按 <c>Hall</c> 算好两个窗口是否还开着):
        /// 赌场窗口没有"id+1 回执"式的关窗信号, 真正决定按钮还在不在的是 <c>Hall.S</c> 与我的
        /// <c>GuessCode</c>/<c>Point</c>, 所以这条必须处理, 否则窗口会一直挂着。
        /// 只处理"关" —— 窗口的**开**只能由 5081/5083 的 offer 触发(否则会给 agent 一个没有 sn 的窗口)。
        /// </summary>
        public void OnGambleState(bool guessOpen, bool diceOpen)
        {
            lock (_lock)
            {
                if (_window == null) return;
                if (!guessOpen && _window.Kind == AgentPendingKind.GambleGuess) _window = null;
                else if (!diceOpen && _window.Kind == AgentPendingKind.GambleDice) _window = null;
            }
        }

        // ---------- 抽奖选号窗口(5041 / 回执 5042) ----------

        /// <summary>
        /// 服务器问"抽奖选哪几个号"(5041 = <c>UI.LandLotteryWindow.DealLand_Lottery</c>, **只有本人**能选)。
        /// offer = <c>LotteryChoiceC2S{Num}</c>(<c>Num</c> = 这次能选几个), 答案是 <c>Info</c> + <c>Vals</c>
        /// (选中的号码) —— 靠 <c>Info.Sn</c> 区分。候选**不在协议里**: 号码范围是
        /// <c>StaticGlobalData.GAME_LAND_LOTTERY_NUMB_LIMIT</c>, 还要刨掉自己已经占了的
        /// (<c>player.Hero.Lotterys</c>), 由观察者用 <c>GameProbe.TrySelfLottery</c> 算好传进来。
        /// 超时回调(<c>OnCompleteSelectLottery</c>)从**最小的可用号码**开始补满 <c>Num</c> 个 → 不答 = 最小的那几个。
        /// </summary>
        public void OnLotteryOffer(long playerId, int chooseNum, IReadOnlyList<int> candidates, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.LotteryPick,
                PlayerId = playerId,
                Ids = ToArray(candidates),
                TargetNum = chooseNum,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>抽奖回执(5042 = LotteryChoiceS2C): 窗口关闭。</summary>
        public void OnLotteryDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.LotteryPick, playerId); }

        // ---------- 追击地块窗口(5033 / 回执 5034) ----------

        /// <summary>
        /// 服务器问"要不要追某个敌方英雄"(5033 = <c>UI.LandPursuitWindow.DealLand_Pursuit</c>) ——
        /// **注意这与 5213 怪物追击不是同一个窗口**(5213 追怪, 5033 追人)。
        /// 这条动作的 <c>Data</c> 客户端**从不解码**(窗口只用 <c>action.Sn</c>), 所以 offer 基本是零负载;
        /// 候选是本地算的: <c>characterType==Hero &amp;&amp; 不是我 &amp;&amp; 不同队 &amp;&amp; !NotSelect</c>,
        /// 再按"还能不能打"过滤(血量&gt;0 且不在医院地块 —— 客户端会把这类行置灰、确定键点不亮),
        /// 口径见 <c>GameProbe.TrySelfPursuitPlayers</c>。候选为空数组是合法结果("现在没人可追")。
        /// 超时回调点的是"停留" → **不答 = 不追**(<c>SelectPlayerId=0</c>)。
        /// </summary>
        public void OnPursuePlayerOffer(long playerId, long[] candidateIds, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.PursuePlayer,
                PlayerId = playerId,
                TargetIds = candidateIds,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>追击地块回执(5034 = PursuitS2C): 窗口关闭。</summary>
        public void OnPursuePlayerDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.PursuePlayer, playerId); }

        // ---------- 助力投票窗口(5309 / 回执 5310 / 选路 5312 / 结束 1093) ----------

        /// <summary>
        /// 服务器问"助力投哪一路"(5309 = <c>AssistVoteLogic.TryShowAssistVote</c>)。
        /// offer = <c>VoteC2S{VoteIds}</c>, 答案是**同一个消息类的** <c>{Info}</c>(只有 sn, **不带选择**) ——
        /// 靠 <c>Info.Sn</c> 区分。这是**两步**窗口:
        /// ① 选路 <c>RequestVoteSelectC2S(monsterId)</c> → <c>VoteSelectC2S{SelectId}</c>(**没有 sn**, 可以反复改);
        /// ② 确认 <c>RequestVoteC2S(sn)</c> → <c>VoteC2S{Info}</c>。
        /// 倒计时回调点的是 <c>SureVote</c>(=第二步) → **不答 = 直接确认**(没选过就等于弃票)。
        /// 候选来自**本地配置**(<c>StaticConfigure.PVEMission.Votes</c> 里 <c>MapId</c> 命中的那组):
        /// 下标 0=右 / 1=左 / 2=中, 82013 图只有右/左, 82015(S7) 图有右/左/中。
        /// 窗口真正的关闭信号是 **1093 PkAfterVoteS2C**(PK 结束), 不是 5310。
        /// </summary>
        public void OnAssistVoteOffer(long playerId, int[] slots, long sn, long nowMs)
        {
            SetWindow(new Window
            {
                Kind = AgentPendingKind.AssistVote,
                PlayerId = playerId,
                Ids = slots,
                Sn = sn,
                SinceMs = nowMs
            });
        }

        /// <summary>助力投票结束(1093 = PkAfterVoteS2C, 该消息**没有 PlayerId**): 关窗(不认人)。</summary>
        public void OnAssistVoteDone(long playerId, long nowMs) { ClearIf(AgentPendingKind.AssistVote, playerId); }

        // ============================== 主线程提示 ==============================

    }
}

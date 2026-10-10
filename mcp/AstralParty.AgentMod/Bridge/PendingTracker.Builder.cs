using System;
using System.Collections.Generic;
using AstralParty.Agent;

namespace AstralParty.AgentMod.Bridge
{
    internal sealed partial class PendingTracker
    {

        public AgentPending Build(
            long selfId,
            bool canThrowDice,
            long currentSn,
            long nowMs,
            Func<string, long, long> remainingOf,
            Func<string, int, string> nameOf)
        {
            var p = new AgentPending();

            Window w;
            int[] moveLands;
            long cardSn;
            int[] usable;
            lock (_lock)
            {
                w = _window;
                moveLands = _moveLands;
                cardSn = _cardSn;
                usable = _usableCards;
            }

            bool windowIsMine = w != null && (selfId == 0 || w.PlayerId == selfId);

            if (windowIsMine)
            {
                p.Kind = w.Kind;
                p.Sn = w.Sn;
                p.Source = "event";
                p.SinceMs = w.SinceMs;
                p.AskPlayerId = w.AskPlayerId;
                p.NoDodge = w.NoDodge;
                p.ResidueCost = w.ResidueCost;
                p.IsAttacker = w.IsAttacker;
                p.Land = w.Land;
                p.VendorCardId = w.VendorCardId;
                p.VendorPrice = w.VendorPrice;
                p.MaxPoint = w.MaxPoint;
                p.TargetNum = w.TargetNum;
                p.TargetIds = w.TargetIds;
                p.GambleCanAct = w.CanAct;
                p.BetGold = w.BetGold;
                FillCandidates(p, w, nameOf);
                FillOptions(p, w, moveLands, usable, currentSn);
                FillDeadline(p, nowMs, remainingOf, w.Sn);
                return p;
            }

            if (canThrowDice)
            {
                p.Kind = AgentPendingKind.ThrowDice;
                p.Sn = currentSn;
                p.Source = "sdk";
                p.Actionable = true;
                p.SinceMs = nowMs;
                p.Options.Add("astral_throw_dice {}                        普通投骰");
                p.Options.Add("astral_throw_dice {\"noOper\":true}           无牌可出直接过");
                p.Options.Add("astral_throw_dice {\"moveNow\":true}          投完立即移动");
                if (usable != null && usable.Length > 0) AddUsableCardNote(p, usable, nameOf);
                FillDeadline(p, nowMs, remainingOf, currentSn);
                return p;
            }

            // 服务器给了可用牌列表 + 一个用牌 sn → 等我对效果牌/快速卡表态
            if (cardSn > 0 && usable != null && usable.Length > 0)
            {
                p.Kind = AgentPendingKind.CardChoice;
                p.Sn = cardSn;
                p.Source = "sdk";
                p.Actionable = true;
                p.SinceMs = nowMs;
                foreach (int id in usable)
                {
                    if (id == 0) continue;
                    p.Candidates.Add(new AgentCandidate { Id = id, Kind = "card", Name = nameOf != null ? nameOf("card", id) : null });
                }
                p.Options.Add("astral_use_effect_card {\"cardId\":<候选里的 id>}   当效果牌打出去");
                p.Options.Add("astral_use_quick_card {\"cardId\":<候选里的 id>, \"targetId\":<被跟玩家>}   跟牌");
                p.Options.Add("astral_abandon_card {\"cardId\":<候选里的 id>}      弃掉");
                p.Notes.Add("这是服务器给的\"可出牌\"列表: 只列了能用的牌, 不代表必须出。");
                FillDeadline(p, nowMs, remainingOf, cardSn);
                return p;
            }

            if (w != null)
            {
                p.Source = "event";
                p.Notes.Add("有候选窗口正在等其他玩家(playerId=" + w.PlayerId + ", kind=" + w.Kind + ")");
            }
            return p;
        }

        private void FillCandidates(AgentPending p, Window w, Func<string, int, string> nameOf)
        {
            // 移动: 候选是本地算出来的地块
            if (w.Kind == AgentPendingKind.Move)
            {
                int[] lands;
                lock (_lock) { lands = _moveLands; }
                if (lands != null)
                {
                    foreach (int id in lands)
                    {
                        if (id == 0) continue;
                        p.Candidates.Add(new AgentCandidate { Id = id, Kind = "land", Name = nameOf != null ? nameOf("land", id) : null });
                    }
                }
                return;
            }

            if (w.Kind == AgentPendingKind.FightCard)
            {
                if (w.Ids == null) return;
                for (int i = 0; i < w.Ids.Length; i++)
                {
                    if (w.Ids[i] == 0) continue;
                    var fc = new AgentCandidate { Id = w.Ids[i], Kind = "card" };
                    if (nameOf != null) fc.Name = nameOf("handCard", fc.Id);
                    if (w.Costs != null && i < w.Costs.Length) fc.Cost = w.Costs[i];
                    p.Candidates.Add(fc);
                }
                return;
            }

            if (w.Kind == AgentPendingKind.PursueMonster)
            {
                if (w.MonsterIds == null) return;
                foreach (long id in w.MonsterIds)
                {
                    if (id == 0) continue;
                    // 怪物 id 是 playerId(64 位), 所以两个字段都填: LongId 给工具用, Id 只作显示/兼容
                    p.Candidates.Add(new AgentCandidate
                    {
                        Id = (int)id,
                        LongId = id,
                        Kind = "monster",
                        Name = nameOf != null ? nameOf("monster", (int)id) : null
                    });
                }
                return;
            }

            if (w.Kind == AgentPendingKind.BatteryTarget || w.Kind == AgentPendingKind.PursuePlayer)
            {
                if (w.TargetIds == null) return;
                foreach (long id in w.TargetIds)
                {
                    if (id == 0) continue;
                    // 英雄 id 是 playerId(64 位): 两个字段都填(LongId 给工具用)
                    p.Candidates.Add(new AgentCandidate
                    {
                        Id = (int)id,
                        LongId = id,
                        Kind = "player",
                        Name = nameOf != null ? nameOf("player", (int)id) : null
                    });
                }
                return;
            }

            if (w.Ids == null) return;
            string kind = CandidateKindOf(w.Kind);
            for (int i = 0; i < w.Ids.Length; i++)
            {
                int id = w.Ids[i];
                if (id == 0) continue;
                var c = new AgentCandidate
                {
                    Id = id,
                    Name = nameOf != null ? nameOf(kind, id) : null,
                    Kind = kind
                };
                if (w.Prices != null && i < w.Prices.Length) c.Price = w.Prices[i];
                if (w.SoldOut != null && i < w.SoldOut.Length) c.SoldOut = w.SoldOut[i];
                if (w.Free != null && i < w.Free.Length) c.Free = w.Free[i];
                p.Candidates.Add(c);
            }
        }

        private void FillOptions(AgentPending p, Window w, int[] moveLands, int[] usable, long currentSn)
        {
            switch (w.Kind)
            {
                case AgentPendingKind.SelectRelic:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_select_relic {\"index\":0..N-1}          选第 index 个候选");
                    p.Options.Add("astral_select_relic {}                     默认选第 0 个");
                    p.Options.Add("astral_select_relic {\"reroll\":true}        重摇这组候选(不结束窗口, 服务器会推新的一组)");
                    p.Notes.Add("重摇后窗口不会关闭: 会来一组新的候选(新 sn), 届时重新决策。");
                    break;

                case AgentPendingKind.RewardCard:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_select_reward_card {\"index\":0..N-1}    选第 index 个候选");
                    break;

                case AgentPendingKind.Move:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_move {\"landId\":<候选里的 id>}         走到候选地块");
                    if (p.Candidates.Count == 0)
                        p.Notes.Add("没算出目标地块(可能不在棋盘上/只有唯一方向由客户端自动走)。");
                    else if (p.Candidates.Count == 1)
                        p.Notes.Add("只有一个方向时客户端通常已经自动走掉, 若仍看到本窗口就直接应答它。");
                    break;

                case AgentPendingKind.Shop:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_shop_buy {\"indexes\":[0,2]}           买第 0、2 格(null 表示空手离店)");
                    p.Options.Add("astral_shop_buy {}                          空手离店");
                    if (w.PveShop) p.Options.Add("astral_atm_transfer {\"targetId\":<队友 playerId>}   给队友转 " + 5 + " 星币(PVE 商店的 ATM)");
                    if (!w.PveShop) p.Notes.Add("这是 PVP 商店(5029): 只支持买/离店。");
                    else p.Notes.Add("这是 PVE 商店(5215): Star 币不足时别硬买。");
                    break;

                case AgentPendingKind.BuyRelic:
                    p.Actionable = true;
                    p.Options.Add("astral_buy_relic {\"confirm\":true}           花 " + w.RelicGold + " 星币买下这个筹码");
                    p.Options.Add("astral_buy_relic {\"confirm\":false}          不买, 离开");
                    p.Notes.Add("筹码价格 RelicGold=" + w.RelicGold + (w.DivinationGold != 0 ? ", DivinationGold=" + w.DivinationGold : ""));
                    p.Notes.Add("★ 判定依据是 Select(2=买 / 0=离开), 不是 Exit —— 客户端确认购买时 Exit 也是 true。");
                    break;

                case AgentPendingKind.CardChoice:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_use_effect_card {\"cardId\":<候选里的 id>}");
                    p.Options.Add("astral_use_quick_card {\"cardId\":<候选里的 id>, \"targetId\":<被跟玩家>}");
                    p.Options.Add("astral_abandon_card {\"cardId\":<候选里的 id>}");
                    break;

                case AgentPendingKind.BattleDice:
                    p.Actionable = true;
                    p.Options.Add("astral_throw_dice {\"battle\":true}        投战斗攻击骰(用本窗口的 sn)");
                    p.Notes.Add("战斗攻击判定(5037)。sn 用本窗口的, 不能用普通回合的投骰 sn。");
                    break;

                case AgentPendingKind.SelectEvent:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_select_event {\"index\":0..N-1}     选第 index 个事件");
                    p.Options.Add("astral_select_event {\"eventId\":<候选里的 id>}   按事件 id 选");
                    p.Options.Add("astral_select_event {}                    默认选第 0 个");
                    p.Notes.Add("超时服务器会代选第 0 项; 候选 id 会原样回传给服务器。");
                    break;

                case AgentPendingKind.AskFight:
                    p.Actionable = true;
                    p.Options.Add("astral_ask_battle {\"accept\":true}          接受这场战斗");
                    p.Options.Add("astral_ask_battle {\"accept\":false}         不打");
                    if (w.AskPlayerId != 0)
                        p.Notes.Add("挑战者(AskPlayerId)=" + w.AskPlayerId + "; 要不要打由你判断(可参考 astral_state 里双方 HP/ATK/DEF)。");
                    p.Notes.Add("★ 超时不答 = 不打(客户端超时回调点的是\"离开\"按钮), 所以想打就得主动答。");
                    break;

                case AgentPendingKind.FightCard:
                    p.Actionable = true;
                    p.Options.Add("astral_use_card {\"cardId\":<候选里的 id>}     出这张战斗牌(候选 id 就是手牌 Guid)");
                    p.Options.Add("astral_use_card {\"pass\":true}              不出牌, 直接过");
                    if (p.Candidates.Count == 0)
                        p.Notes.Add("没有可出的战斗牌(手牌里没有匹配我方角色的牌, 或都买不起)。");
                    p.Notes.Add("我方角色: " + (w.IsAttacker ? "攻击方(只能用攻击类牌)" : "防守方(只能用防御类牌)") +
                                (w.ResidueCost >= 0 ? "; 剩余战斗点数=" + w.ResidueCost : "") +
                                "; 候选的 Cost 就是这张牌要花的点数, 超过剩余点数服务器会拒。");
                    p.Notes.Add("★ 超时不答 = 不出牌(CardUid=0)。");
                    break;

                case AgentPendingKind.FightChoice:
                    p.Actionable = true;
                    if (w.NoDodge)
                    {
                        p.Options.Add("astral_battle_choice {\"dodge\":false}      硬吃(本回合不能闪避)");
                        p.Notes.Add("★ NoDodge=true: 客户端会直接拒绝闪避请求, 只能回 dodge=false。");
                    }
                    else
                    {
                        p.Options.Add("astral_battle_choice {\"dodge\":true}       闪避");
                        p.Options.Add("astral_battle_choice {\"dodge\":false}      硬吃");
                    }
                    p.Notes.Add("★ 超时不答 = 不闪避。");
                    break;

                case AgentPendingKind.StopOrContinue:
                    p.Actionable = true;
                    p.Options.Add("astral_stop_or_continue {\"stop\":false}      继续走");
                    p.Options.Add("astral_stop_or_continue {\"stop\":true}       就地停留");
                    p.Notes.Add("加油站/出生点(5077)。信息全在本地: " +
                                (string.IsNullOrEmpty(w.Land) ? "没读到当前地块" : "当前地块=" + w.Land) +
                                "; 其余看 astral_state 里的星币/等级/分数。");
                    p.Notes.Add("★ 超时不答 = 继续走(客户端超时回调点的是\"继续\"按钮)。");
                    break;

                case AgentPendingKind.ReviveTeammate:
                    p.Actionable = true;
                    p.Options.Add("astral_revive_teammate {\"revive\":true}     复活队友(花星币)");
                    p.Options.Add("astral_revive_teammate {\"revive\":false}    不复活");
                    p.Notes.Add("复活队友(5233)。要花多少星币、谁倒下了看 astral_state 的 Gold 与全体单位血量。");
                    p.Notes.Add("★ 超时不答 = 不复活(客户端超时回调点的是\"继续\"按钮)。");
                    break;

                case AgentPendingKind.SelectMechanism:
                    p.Actionable = true;
                    p.Options.Add("astral_select_mechanism {\"select\":true}    启动");
                    p.Options.Add("astral_select_mechanism {\"select\":false}   不启动");
                    p.Notes.Add("机制选择(5259)。");
                    p.Notes.Add("★ 超时不答 = 不启动(客户端超时回调点的是\"继续\"按钮)。");
                    break;

                case AgentPendingKind.HospitalCheck:
                    p.Actionable = true;
                    p.Options.Add("astral_hospital_check {}                    接受检查");
                    p.Notes.Add("医院(5093): 这条窗口**只有\"检查\"一个合法上行** —— 客户端另一个按钮(\"没病\")" +
                                "只切本地视图、不发包; 倒计时结束点的是\"检查\"。");
                    p.Notes.Add("★ 超时不答 = 客户端自己发\"检查\", 所以答与不答的效果一样。");
                    break;

                case AgentPendingKind.PursueMonster:
                    p.Actionable = true;
                    if (p.Candidates.Count > 0)
                        p.Options.Add("astral_pursue_monster {\"monsterId\":<候选里的 LongId>}   追击这只怪");
                    p.Options.Add("astral_pursue_monster {\"pass\":true}                         不追击");
                    if (p.Candidates.Count == 0)
                        p.Notes.Add("本地没算出可追的怪(怪物要么已被选走、要么在医院地块、要么同队)。此时只能不追。");
                    p.Notes.Add("★ 超时不答 = 不追击(SelectId=0)。");
                    break;

                case AgentPendingKind.VendorCard:
                    p.Actionable = true;
                    p.Options.Add("astral_vendor_buy_card {\"buy\":true}      花 " + w.VendorPrice + " 星币买下");
                    p.Options.Add("astral_vendor_buy_card {\"buy\":false}     不买");
                    p.Notes.Add("商人买卡(5323): 价格=" + w.VendorPrice + " 星币" +
                                (w.VendorCardId != 0 ? ", 卡牌 id=" + w.VendorCardId : "") +
                                "; 星币不足时客户端会拒绝购买请求。");
                    p.Notes.Add("★ 超时不答 = 不买(客户端超时回调点的是\"取消\"按钮)。");
                    break;

                case AgentPendingKind.SelectPoint:
                    p.Actionable = true;
                    p.Options.Add("astral_select_point {\"point\":1.." + w.MaxPoint + "}     用几点移动力");
                    p.Notes.Add("控制移动卡(5067): 可选点数 1.." + w.MaxPoint + "。");
                    p.Notes.Add("★ 超时不答 = 1 点(客户端超时会把点数兜成 1 再确定)。");
                    break;

                case AgentPendingKind.BatteryTarget:
                    p.Actionable = true;
                    {
                        int candCount = w.TargetIds == null ? -1 : w.TargetIds.Length;
                        p.Options.Add("astral_battery_pick {\"targetIds\":[id,...]}    选 1.." + w.TargetNum +
                                      " 个英雄(用候选里的 playerId/LongId)");
                        p.Options.Add("astral_battery_pick {\"leave\":true}              不选目标, 直接离开");
                        if (candCount >= 0)
                            p.Notes.Add("炮台选目标(5063): 最多选 " + w.TargetNum + " 个英雄, 候选有 " + candCount +
                                        " 个(见 pending 候选, kind=player)。");
                        else
                            p.Notes.Add("炮台选目标(5063): 最多选 " + w.TargetNum +
                                        " 个英雄; 候选还没读出来(战斗数据未就绪), 此时只能 leave=true。");
                        p.Notes.Add("★ 超时不答 = 离开(客户端超时回调点的是\"离开\"按钮, Exit=true)。");
                    }
                    break;

                case AgentPendingKind.Divination:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_divination_pick {\"index\":0..N-1}     选第 index 张占卜牌");
                    p.Options.Add("astral_divination_pick {\"divinationId\":<候选里的 id>}   按占卜卡 id 选");
                    p.Options.Add("astral_divination_pick {}                    默认选第 0 张");
                    p.Notes.Add("占卜(5069): 两张里选一张(候选见 pending, kind=divination)。");
                    p.Notes.Add("★ 超时不答 = 选第 1 张(客户端超时回调点的是第 1 张牌)。");
                    break;

                case AgentPendingKind.GambleGuess:
                    p.Actionable = w.CanAct;
                    p.Options.Add("astral_gamble_guess {\"guessCode\":1}   押奇数");
                    p.Options.Add("astral_gamble_guess {\"guessCode\":2}   押偶数");
                    p.Notes.Add("赌场押注(5081): 本注 " + w.BetGold + " 星币。");
                    if (!w.CanAct)
                        p.Notes.Add("**你这边按钮是灰的(已死/星币不足), 桥接会拒答** —— 客户端自己的超时仍会替你押奇数。");
                    p.Notes.Add("★ 超时不答 = 押奇数(GuessCode=1)。");
                    break;

                case AgentPendingKind.GambleDice:
                    p.Actionable = w.CanAct;
                    p.Options.Add("astral_gamble_dice {}                掷骰(唯一合法上行)");
                    if (!w.CanAct)
                        p.Notes.Add("**你这边按钮是灰的(已死/星币不足), 桥接会拒答** —— 客户端自己的超时仍会替你掷。");
                    p.Notes.Add("★ 超时不答 = 也发掷骰(客户端超时点的就是这个按钮)。");
                    break;

                case AgentPendingKind.LotteryPick:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_lottery_pick {\"numbers\":[n1,n2,...]}   选 " + w.TargetNum +
                                  " 个还没被你占的号码(用候选里的数字)");
                    p.Options.Add("astral_lottery_pick {}                          默认选最小的那几个(与客户端超时一致)");
                    p.Notes.Add("抽奖(5041): 这次要选 " + w.TargetNum + " 个号码; 候选 = 1..上限里**你还没占**的号码(见候选, kind=lottery)。");
                    p.Notes.Add("★ 超时不答 = 从最小的可用号码开始补满 " + w.TargetNum + " 个。");
                    break;

                case AgentPendingKind.PursuePlayer:
                    p.Actionable = p.Candidates.Count > 0;
                    p.Options.Add("astral_pursue_player {\"playerId\":<候选里的 id>}   追这个敌方英雄");
                    p.Options.Add("astral_pursue_player {\"stay\":true}                不追, 就地停留");
                    {
                        int candCount = w.TargetIds == null ? -1 : w.TargetIds.Length;
                        if (candCount > 0)
                            p.Notes.Add("追击地块(5033): 候选有 " + candCount +
                                        " 个敌方英雄(见候选, kind=player); 只有血量>0 且不在医院地块的才算能追。");
                        else if (candCount == 0)
                            p.Notes.Add("追击地块(5033): 现在没有能追的敌方英雄(要么不在场、要么血量 0/在医院)。");
                        else
                            p.Notes.Add("追击地块(5033): 候选还没读出来(战斗数据未就绪), 此时只能 stay=true。");
                    }
                    p.Notes.Add("★ 超时不答 = 不追(SelectPlayerId=0, 客户端超时点的是\"停留\"按钮)。");
                    break;

                case AgentPendingKind.AssistVote:
                    {
                        p.Actionable = true;
                        int right = w.Ids != null && w.Ids.Length > 0 ? w.Ids[0] : 0;
                        int left = w.Ids != null && w.Ids.Length > 1 ? w.Ids[1] : 0;
                        int center = w.Ids != null && w.Ids.Length > 2 ? w.Ids[2] : 0;
                        p.Options.Add("astral_assist_vote_select {\"side\":\"left\"|\"right\"|\"center\"}   选一路(可反复改)");
                        p.Options.Add("astral_assist_vote_sure {}                                    确认这张票");
                        p.Notes.Add("助力投票(5309, 两步): 左=" + left + " / 右=" + right + " / 中=" + center +
                                    "(0 = 本图没有这一路)。先 select 再 sure。");
                        p.Notes.Add("★ 超时不答 = **直接确认**(没选过就等于弃票, 客户端超时点的是\"确认\"按钮)。");
                    }
                    break;
            }

            if (usable != null && usable.Length > 0 && w.Kind != AgentPendingKind.CardChoice)
                AddUsableCardNote(p, usable, null);
        }

        private static void AddUsableCardNote(AgentPending p, int[] usable, Func<string, int, string> nameOf)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < usable.Length; i++)
            {
                if (usable[i] == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(usable[i]);
                if (nameOf != null)
                {
                    string n = nameOf("card", usable[i]);
                    if (!string.IsNullOrEmpty(n)) sb.Append('(').Append(n).Append(')');
                }
            }
            if (sb.Length > 0) p.Notes.Add("服务器标记为\"当前可用\"的牌: [" + sb + "]");
        }

        private static void FillDeadline(AgentPending p, long nowMs, Func<string, long, long> remainingOf, long sn)
        {
            p.DeadlineMs = 0;
            p.RemainingMs = -1;
            if (remainingOf == null) return;
            long remaining;
            try { remaining = remainingOf(p.Kind, sn); }
            catch { return; }
            if (remaining < 0) return;
            p.RemainingMs = remaining;
            p.DeadlineMs = nowMs + remaining;
        }

        private static string CandidateKindOf(string windowKind)
        {
            switch (windowKind)
            {
                case AgentPendingKind.SelectRelic: return "relic";
                case AgentPendingKind.RewardCard: return "rewardCard";
                case AgentPendingKind.Shop: return "shopCard";
                case AgentPendingKind.Move: return "land";
                case AgentPendingKind.SelectEvent: return "event";
                case AgentPendingKind.PursueMonster: return "monster";
                case AgentPendingKind.Divination: return "divination";
                case AgentPendingKind.LotteryPick: return "lottery";
                case AgentPendingKind.AssistVote: return "monster";
                default: return "unknown";
            }
        }

    }
}

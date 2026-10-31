using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using Core.Net;
using GameLogic;
using party.model;
using Tools;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ExtremeDifficultyMod
{
    public sealed partial class ExtremeDifficultyMod : ModBase
    {
        // =====================================================================
        // 日志包装: 日志自身异常绝不允许冒泡到调用方
        // =====================================================================

        private static void LogInfo(string msg) { try { SdkLog.Info(Tag, msg); } catch { } }
        private static void LogWarn(string msg) { try { SdkLog.Warn(Tag, msg); } catch { } }
        private static void LogError(string msg) { try { SdkLog.Error(Tag, msg); } catch { } }

        // =====================================================================
        // 探测: 服务器到底认不认 difficulty=4
        // =====================================================================

        /// <summary>
        /// 观察当前房间的 (地图, 难度) 并在变化时打日志。
        /// 这是判断"服务器是否接受极限"的唯一可靠信号 —— 因为客户端会无条件
        /// 采用服务器回传的难度值。
        /// </summary>
        private void ProbeRoom()
        {
            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;

            GameLogicManager mgr = SimpleSingletonProvider<GameLogicManager>.inst;
            if (mgr == null || mgr.room == null) return;

            if (!mgr.room.IsInRoom)
            {
                _lastMapId = -1;
                _lastDiff = -1;
                return;
            }

            RoomInfo info = mgr.room.curRoomInfo;
            if (info == null) return;

            int mapId = info.MapDifficultyId;
            int diff = info.Difficulty;
            if (mapId == _lastMapId && diff == _lastDiff) return;

            _lastMapId = mapId;
            _lastDiff = diff;

            string name = (diff >= 0 && diff < DiffNames.Length) ? DiffNames[diff] : ("index " + diff);
            LogInfo("房间状态: map=" + mapId + " difficulty=" + diff + " (" + name + ")"
                + (diff == ExtremeIndex ? "  <== 服务器接受了极限" : ""));
        }

        /// <summary>
        /// <b>只读</b>监视「组队匹配」的队伍状态 —— 不需要任何热键。
        ///
        /// <para>
        /// 为什么单独盯它：「极限挑战」的正规入口是**匹配（组队）**而不是房间列表
        /// （见 agent.md §16.7）。玩家自己在界面上点「匹配 → PvE」建队时，
        /// 服务器回的 <c>MatchTeamInfo</c> 会被 <c>MatchData.UpdateMatchInfo</c> 写进本地：
        /// </para>
        /// <code>
        /// _matchMapId = matchInfo.MapId;
        /// _matchMode = matchInfo.Mode;
        /// _matchDifficulty = matchInfo.Difficulty;   // ← 这是**服务器**给的值
        /// </code>
        /// <para>
        /// 所以只要队伍建立成功，<c>MatchDifficulty</c> 就等于服务器的裁决结果：
        /// 它等于 4 说明服务器<b>认了</b>极限；被钳回 3 或被拒（<c>InTeam</c> 一直为 false）同样一眼可见。
        /// 这条路径完全是被动读取，不碰任何游戏状态，是热键之外的安全网。
        /// </para>
        /// </summary>
        private void ProbeMatchTeam()
        {
            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;

            GameLogicManager mgr = SimpleSingletonProvider<GameLogicManager>.inst;
            if (mgr == null || mgr.match == null || mgr.match.matchData == null) return;

            MatchData md = mgr.match.matchData;
            if (!md.InTeam)
            {
                if (_teamLogged)
                {
                    LogInfo("匹配队伍: 已离队");
                    _teamLogged = false;
                    _lastTeamId = -1;
                    _lastTeamMap = -1;
                    _lastTeamDiff = -1;
                }
                return;
            }

            long teamId = md.TeamId;
            int mapId = md.MatchMapId;
            int diff = md.MatchDifficulty;
            if (_teamLogged && teamId == _lastTeamId && mapId == _lastTeamMap && diff == _lastTeamDiff) return;

            _teamLogged = true;
            _lastTeamId = teamId;
            _lastTeamMap = mapId;
            _lastTeamDiff = diff;

            string name = (diff >= 0 && diff < DiffNames.Length) ? DiffNames[diff] : ("index " + diff);
            LogInfo("匹配队伍: teamId=" + teamId + " map=" + mapId + " mode=" + md.MatchMode
                + " difficulty=" + diff + " (" + name + ") 人数=" + md.TeamPlayers.Count);

            if (diff == ExtremeIndex)
            {
                LogInfo("  <== 服务器把难度定成极限了, 这条(匹配)路是通的!");
            }
        }

        // =====================================================================
        // 第二道门(只影响低等级账号): UnLockDifficulty 置灰
        // =====================================================================

        /// <summary>
        /// 难度项被置灰有个<b>等级前提</b>: <c>UICom_RoomSetting.RefreshDifficulty</c> 里
        /// <code>
        /// if (playerInfo != null &amp;&amp; playerInfo.Level &lt; StaticGlobalData.ROOM_PVELOCK_LEVEL)
        ///     btn.grayed = index &gt;= ROOM_PVELOCK_DIFFICULTY &amp;&amp; index &gt; unLockDifficulty;
        /// </code>
        /// <c>MatchLogic.IsVailDifficulty</c> 用的是同一个条件。也就是说<b>等级 ≥ 10 的账号根本不会被置灰</b>,
        /// 光清时间窗就能选极限。
        ///
        /// <para>
        /// 而置灰<b>不是装饰</b> —— <c>UICom_RoomSetting.DifficultyChange</c> 开头就是
        /// <c>if (btn_Info.grayed) { ShowTips(11025); return; }</c>(11025 = "需要先通关上一难度。"),
        /// 会直接拦掉选择。所以低等级账号还需要把本地 UnLockDifficulty 抬到极限档,
        /// 让 <c>index &gt; unLockDifficulty</c> 恒为假。
        /// </para>
        /// <para>
        /// 这是<b>纯本地 UI 解锁</b>: UnLockDifficulty 是服务器权威数据(由 UnLockDifficultyS2C 下发),
        /// 服务器下次同步就覆盖回来。等级达标时本方法不做任何事。
        /// </para>
        /// </summary>
        private void RelaxUnlockGate()
        {
            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;

            GameLogicManager mgr = SimpleSingletonProvider<GameLogicManager>.inst;
            if (mgr == null || mgr.account == null) return;

            Player p = mgr.account.GetPlayerInfo();
            if (p == null) return;

            int levelGate = StaticGlobalData.ROOM_PVELOCK_LEVEL;
            if (p.Level >= levelGate) return;            // 等级达标 -> 游戏不会置灰, 不动任何数据
            if (p.UnLockDifficulty >= ExtremeIndex) return;

            int before = p.UnLockDifficulty;
            p.UnLockDifficulty = ExtremeIndex;
            LogWarn("账号等级 " + p.Level + " < " + levelGate + ": 本地 UnLockDifficulty "
                + before + " -> " + ExtremeIndex + " (仅解除难度下拉置灰, 服务器可能覆盖)");
        }

        // =====================================================================
        // 诊断(F9): 直接问服务器 —— 组队匹配端点到底收不收 difficulty=4
        // =====================================================================

        /// <summary>触发键: 游戏里按 F9 执行一次主动探测。</summary>
        private static readonly KeyCode ProbeKey = KeyCode.F9;

        /// <summary>上一帧探测键是否按住, 用于自己做下降沿检测。</summary>
        private bool _probeKeyWasHeld;

        /// <summary>
        /// 热键判定。<b>刻意用 <see cref="InputService.IsKeyHeld"/> 自己做下降沿</b>,
        /// 而不是只依赖 <see cref="InputService.IsKeyPressed"/>。
        ///
        /// <para>
        /// <c>IsKeyPressed</c> 等价于 <c>Input.GetKeyDown</c>, 只在"按下那一帧"为真 —— 掉一帧就丢。
        /// v1.0.2 正是栽在这上面: 调用点被 <c>% 120</c> 节流挡住, 用户按了多次也没命中采样帧,
        /// 日志里一行探测都没有。改用 <c>IsKeyHeld</c> 这种电平量自己算下降沿, 就与帧率/掉帧无关了。
        /// </para>
        /// </summary>
        private void ProbeOnHotkey()
        {
            bool held = InputService.IsKeyHeld(ProbeKey);
            bool edge = held && !_probeKeyWasHeld;
            _probeKeyWasHeld = held;

            if (edge || InputService.IsKeyPressed(ProbeKey)) RunProbe();
        }

        /// <summary>
        /// 主动探测。为什么要这么做：
        ///
        /// <para>
        /// 已确认的实机结论是"客户端能看见极限，但服务器回 <c>Code.NotOpen = 10015</c>（开启时间未到）"。
        /// 但那条结论是从<b>界面表现 + 错误码表</b>推出来的 —— SDK 没有 hook 能力，拦不到游戏自己发的 RPC，
        /// 所以日志里从来没见过真正的 <c>errId</c>。
        /// </para>
        /// <para>
        /// 这个探测替玩家发一次 <c>CreateMatchTeamC2S</c>（模式 Pve），把服务器应答的 <c>errId</c> 原样打出来。
        /// 关键点：
        /// </para>
        /// <list type="bullet">
        ///   <item>它是<b>另一个服务器端点</b>（组队匹配），和"快速加入 / 开房间"不是同一条路；</item>
        ///   <item>带上本 mod 之后 <c>MatchData.GetDefaultMapInfo(Pve)</c> 返回 <c>(82010, 4)</c>，
        ///         所以这次请求的 payload 里<b>确实带着 difficulty=4</b>；</item>
        ///   <item>若 <c>errId == 0</c>，说明服务器在这个端点上<b>没有</b>按排期卡难度 —— 那就是一条可玩的路。</item>
        /// </list>
        /// <para>
        /// 已经在队伍里时不重复创建（避免污染状态），只把服务器回传的队伍状态打出来 ——
        /// 那同样是硬数据，因为 <c>MatchMapId / MatchDifficulty</c> 是<b>服务器</b>给的。
        /// </para>
        /// </summary>
        private void RunProbe()
        {
            LogInfo("=== 探测开始 (F9) ===");

            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance)
            {
                LogWarn("探测中止: GameLogicManager 还没实例化(还在启动阶段?)");
                return;
            }

            GameLogicManager mgr = SimpleSingletonProvider<GameLogicManager>.inst;
            if (mgr == null || mgr.match == null || mgr.match.matchData == null)
            {
                LogWarn("探测中止: match / matchData 不可用");
                return;
            }

            // ---------- 1) 客户端侧: mod 到底改出了什么 ----------
            Player p = (mgr.account != null) ? mgr.account.GetPlayerInfo() : null;
            if (p != null)
            {
                LogInfo("账号: Level=" + p.Level + " UnLockDifficulty=" + p.UnLockDifficulty
                    + "  (置灰门槛: Level < " + StaticGlobalData.ROOM_PVELOCK_LEVEL + ")");
            }

            MapConfigure cfg = StaticConfigure.Map;
            for (int m = 0; m < ExtremeMaps.Length; m++)
            {
                int mapId = ExtremeMaps[m];
                MapMapLevelConfigure row;
                if (cfg == null || !cfg.MapLevelDict.TryGetValue(mapId, out row) || row == null) continue;

                var items = row.MapMapLevelConfigureItems;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] == null || items[i].Index != ExtremeIndex) continue;
                    bool open = items[i].BeginTime == null && items[i].EndTime == null;
                    LogInfo("客户端时间窗: map=" + mapId + " Index=" + ExtremeIndex + " -> "
                        + (open ? "已清空(ValidityTime 恒真)" : "!! 仍被挡住"));
                }
            }

            // ---------- 2) mod 对"匹配默认选图"的影响 ----------
            MatchData md = mgr.match.matchData;
            try
            {
                var def = md.GetDefaultMapInfo((int)MapModeType.Pve);
                LogInfo("匹配默认 payload: MapId=" + def.Item1 + " Difficulty=" + def.Item2
                    + (def.Item2 == ExtremeIndex ? "  (客户端已把极限排在队首)" : "  (没取到极限)"));
            }
            catch (Exception e) { LogWarn("GetDefaultMapInfo 异常: " + e.Message); }

            // ---------- 3) 服务器侧: 直接问 ----------
            if (md.InTeam)
            {
                LogInfo("已在队伍中(不重复创建)。服务器回传的队伍: map=" + md.MatchMapId
                    + " difficulty=" + md.MatchDifficulty + " mode=" + md.MatchMode
                    + " teamId=" + md.TeamId);
                LogInfo(md.MatchDifficulty == ExtremeIndex
                    ? "  <== 服务器给的难度就是极限, 这条路通了!"
                    : "  <== 服务器给的难度不是极限");
                return;
            }

            LogInfo("向服务器发请求: CreateMatchTeamC2S{Mode=Pve}, payload 带 difficulty=" + ExtremeIndex);
            RPCAsyncResult res = mgr.match.RequestCreateMatchTeamC2S(MapModeType.Pve);
            if (res == null)
            {
                LogError("探测失败: 请求返回 null");
                return;
            }

            res.OnFinished.AddOnce(delegate (RPCAsyncResult r)
            {
                try
                {
                    LogInfo("服务器应答: errId=" + r.errId + " -> " + DescribeErr(r.errId));

                    if (r.errId == 0)
                    {
                        MatchData d = mgr.match.matchData;
                        LogInfo("  <== 服务器**接受**了! 队伍: map=" + d.MatchMapId
                            + " difficulty=" + d.MatchDifficulty + " mode=" + d.MatchMode
                            + "  (去匹配界面看能不能开局)");
                    }
                    else
                    {
                        LogInfo("  <== 服务器拒绝, 和界面上的提示对得上");
                    }
                }
                catch (Exception e) { LogError("探测回调异常: " + e.Message); }
            });
        }

        /// <summary>
        /// 把服务器错误码翻成人话。只硬编码几个关键码，不去查 <c>STRServer</c> 表 ——
        /// 少一个运行期类型引用，就少一分在 HybridCLR 上踩 <c>MethodNotFind</c> 的风险（见 §16.6）。
        /// </summary>
        private static string DescribeErr(int errId)
        {
            switch (errId)
            {
                case 0: return "成功";
                case 10003: return "账号被封禁";
                case 10012: return "客户端版本过低";
                case 10013: return "无效参数";
                case 10015: return "开启时间未到 (Code.NotOpen) <== 就是界面上那句";
                case 10016: return "条件不足";
                case 10017: return "重复领奖";
                case 10020: return "登录异常";
                default: return "错误码 " + errId + " (查 STRServer 表)";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using CesiumLoader.SDK;
using Core.Net;
using GameLogic;
using party.model;
using Tools;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ExtremeDifficultyMod
{
    /// <summary>
    /// mod 入口。加载器约定: <c>mods\{ModId}\{ModId}.dll</c> 里必须有 <c>{文件名}.ModEntry.Main()</c>。
    /// </summary>
    public static class ModEntry
    {
        public static void Main()
        {
            // 新式生命周期: ModBase 自动注册 ModContext / 主线程回调 / 场景事件, 卸载时自动退订。
            // 默认 30 秒启动延迟 —— 游戏启动早期碰 GameLogicManager 单例会崩。
            ModBase.Run(new ExtremeDifficultyMod());
        }
    }

    /// <summary>
    /// 极限难度解锁。
    ///
    /// <para><b>为什么需要它</b> —— 逆向结论, 详见 docs/水乡古镇-极限难度.md:</para>
    /// <para>
    /// 房间设置的难度下拉由 <c>UICom_RoomSetting.UpdateDifficultyContent</c> 构建,
    /// 每个档位只有满足下面这条才会被加进列表:
    /// </para>
    /// <code>TimeHelper.ValidityTime(levelItem.BeginTime, levelItem.EndTime)</code>
    /// <para>
    /// 而 <c>Map.bin</c> 里这些字段<b>全是空的</b> —— 真正的值来自 <c>FixMap.bin</c>,
    /// 启动时由 <c>StaticConfigure.InitAsync → VerifyStaticConfig.StartFix →
    /// MapConfigure.Fix(FixMap) → MapMapLevelConfigureItem.FixData()</c> 覆盖写入。
    /// </para>
    /// <para>
    /// 「极限」(难度 Index=4) 的时间窗是官方活动排期, 全表只有 3 张 PvE 图定义过:
    /// </para>
    /// <list type="bullet">
    ///   <item>82008 御魂庆典　：2025-07-04 .. 2026-01-16</item>
    ///   <item>82010 水乡古镇　：2026-03-16 .. 2026-05-20</item>
    ///   <item>82007 星趴·梦想号：2026-05-20 .. 2026-06-24</item>
    /// </list>
    /// <para>
    /// 三段窗口都已过期, 所以现在客户端不再显示「极限」。
    /// </para>
    ///
    /// <para><b>本 mod 做的事</b></para>
    /// <para>
    /// 把这三张图第 5 档的 <c>BeginTime/EndTime</c> 清空 (<c>ValidityTime</c> 对
    /// begin==null 且 end==null 恒返回 true) → 「极限」重新出现在难度下拉里。
    /// 用的是官方自己的 <c>FixData()</c> 入口, 不碰任何 private 字段。
    /// </para>
    ///
    /// <para><b>本 mod 做不到的事(重要)</b></para>
    /// <para>
    /// 难度是要发给服务器的: <c>ChangeRoomC2S{Difficulty=4}</c> → 服务器回
    /// <c>ChangeRoomS2C{Room}</c> → 客户端 <c>RoomController.UpdateRoomSetting</c>
    /// <b>无条件采用服务器回传的值</b>。所以"服务器是否接受 difficulty=4"无法在
    /// 客户端侧保证 —— 只能实测。本 mod 会持续观察
    /// <c>RoomInfo.MapDifficultyId / RoomInfo.Difficulty</c> 并把变化打进日志:
    /// 选完极限后若日志出现 <c>difficulty 4 (极限)</c>, 说明服务器接受了;
    /// 若仍是 0..3 或没有变化, 说明服务器拒绝/钳制了。
    /// </para>
    ///
    /// <para><b>安全边界</b></para>
    /// <para>
    /// 只对"确实定义了第 5 档"的地图生效。给只有 4 档的图(如 82016 异变图书馆)强塞
    /// difficulty=4 会让 <c>PerformTriggerLogic</c> 里的 <c>items[Difficulty]</c> 越界 ——
    /// 所以本 mod <b>只清时间窗, 绝不修改任何地图的档位数量</b>, 也不替玩家发送难度。
    /// </para>
    ///
    /// <para><b>v1.0.1 修复(实机日志踩出来的坑, 见 agent.md §16.6)</b></para>
    /// <para>
    /// v1.0.0 在实机里 <c>Apply 异常: MethodNotFind System.DateTimeOffset::FromUnixTimeSeconds</c>
    /// 每 1.3 秒刷一次, <b>补丁一次都没生效</b>。两个错误叠加:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     HybridCLR/IL2CPP 下 <c>DateTimeOffset.FromUnixTimeSeconds</c> 不在 AOT 元数据里,
    ///     调用即失败; 而这类失败<b>发生在方法被解析/编译时</b>, 所以
    ///     <c>try { ... } catch { }</c> 写在同一个方法里是<b>无效</b>的 ——
    ///     正是 SDK 文档 `SDK-Unity调用与ECall隔离.md` 第 5 节写明的反模式。
    ///     现在改成<b>纯整数运算</b>换算日期, 完全不碰 BCL 日期 API, 从根上消除。
    ///   </item>
    ///   <item>
    ///     更致命的是<b>顺序</b>: v1.0.0 先 <c>Fmt()</c> 拼日志、后 <c>FixData()</c> 打补丁,
    ///     于是格式化一失败就把补丁一起带走了。<b>现在补丁先落地, 格式化和日志全部在其后</b>,
    ///     并且日志包在安全包装里 —— <b>日志绝不能让修复失败</b>。
    ///   </item>
    /// </list>
    /// <para>
    /// 补丁落地后还会<b>读回自检</b>, 日志里明确打印"校验通过/校验失败", 不再靠推断。
    /// </para>
    /// </summary>
    public sealed class ExtremeDifficultyMod : ModBase
    {
        private const string Tag = "ExtremeDifficulty";

        /// <summary>「极限」在难度阶梯里的 Index (0..4 = 普通/困难/噩梦/疯狂/极限)。</summary>
        private const int ExtremeIndex = 4;

        /// <summary>
        /// 全表仅有这三张 PvE 图定义了 <c>Index=4</c> 的难度行。
        /// 其它 PvE 图只有 0..3, 不在清理范围内。
        /// </summary>
        private static readonly int[] ExtremeMaps = { 82010, 82007, 82008 };

        /// <summary>难度显示名(仅用于日志; 正式名称来自 STRChoosingTimeLimit[21+n])。</summary>
        private static readonly string[] DiffNames = { "普通", "困难", "噩梦", "疯狂", "极限" };

        private readonly HashSet<int> _cleared = new HashSet<int>();
        private int _frame;
        private int _lastMapId = -1;
        private int _lastDiff = -1;
        private bool _announced;

        // 「组队匹配」队伍状态的只读监视(见 ProbeMatchTeam)
        private bool _teamLogged;
        private long _lastTeamId = -1;
        private int _lastTeamMap = -1;
        private int _lastTeamDiff = -1;

        public override string Name { get { return "极限难度解锁"; } }

        // ModBase.Version 默认返回 "1.0.0"。不覆写的话, SDK 的生命周期日志会一直显示 1.0.0
        // (加载器读的是 [ModManifest]/json, 两个来源不同), 徒增排查噪音。覆写对齐。
        public override string Version { get { return "1.0.4"; } }

        public override void OnInitialize()
        {
            LogInfo("启动(v" + Version + ")。目标: "
                + string.Join(", ", Array.ConvertAll(ExtremeMaps, m => m.ToString()))
                + " 的『极限』档 (Index=" + ExtremeIndex + ")");
            LogInfo("提示: 本 mod 只能解开客户端 UI 的门; 是否真能开局由服务器裁决, 结果会打在下面。");
            LogInfo("热键: 按 " + ProbeKey + " 主动探测服务器(会把服务器返回的 errId 打进本日志)。输入后端: "
                + InputService.BackendName + " (可用=" + InputService.IsAvailable + ")");

            // 各自独立 try/catch: 任何一个环节出问题都不能让初始化整体失败。
            try { RelaxUnlockGate(); }
            catch (Exception e) { LogWarn("解锁门槛处理异常: " + e.Message); }

            try { Apply("init"); }
            catch (Exception e) { LogError("init 阶段 Apply 异常: " + e.Message); }
        }

        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // StaticConfigure.Clear() 会把 _Map 置空, 重新进主界面时配置会再加载一遍 ->
            // 之前清掉的时间窗会随新对象一起回来, 所以这里要重新清一次。
            _cleared.Clear();
            _announced = false;
            try { Apply("scene"); }
            catch (Exception e) { LogError("场景后重清异常: " + e.Message); }
        }

        public override void OnUpdate()
        {
            // 热键必须**每帧**检查, 绝不能放进下面的节流里 —— 见 ProbeOnHotkey 的注释。
            try { ProbeOnHotkey(); }
            catch (Exception e) { LogWarn("探测异常: " + e.Message); }

            // 其余检查每帧跑没必要; 约 2 秒一次足够覆盖配置重载(lazy 兜底)。
            if (++_frame % 120 != 0) return;

            try { Apply("watch"); }
            catch (Exception e) { LogError("Apply 异常: " + e.Message); }

            try { ProbeRoom(); }
            catch (Exception e) { LogWarn("ProbeRoom 异常: " + e.Message); }

            try { ProbeMatchTeam(); }
            catch (Exception e) { LogWarn("ProbeMatchTeam 异常: " + e.Message); }

            try { RelaxUnlockGate(); }
            catch (Exception e) { LogWarn("RelaxUnlockGate 异常: " + e.Message); }
        }

        // =====================================================================
        // 核心: 清空「极限」档的时间窗
        // =====================================================================

        /// <summary>
        /// 把目标地图第 5 档的 BeginTime/EndTime 置空。幂等 —— 已经是空的就什么都不做。
        ///
        /// <para>
        /// 顺序纪律(踩过坑): <b>先读原始秒数 → 再打补丁 → 再自检 → 最后才格式化/写日志</b>。
        /// 任何格式化或日志的失败都不允许影响补丁落地。
        /// </para>
        /// </summary>
        private void Apply(string reason)
        {
            MapConfigure cfg = StaticConfigure.Map;
            if (cfg == null) return;   // 配置还没加载完, 下次再试

            for (int m = 0; m < ExtremeMaps.Length; m++)
            {
                int mapId = ExtremeMaps[m];

                MapMapLevelConfigure row;
                if (!cfg.MapLevelDict.TryGetValue(mapId, out row) || row == null) continue;

                var items = row.MapMapLevelConfigureItems;
                if (items == null) continue;

                for (int i = 0; i < items.Count; i++)
                {
                    MapMapLevelConfigureItem item = items[i];
                    if (item == null || item.Index != ExtremeIndex) continue;

                    // ① 只做字段读取, 不格式化 —— 这一步不会失败。
                    bool hadBegin = item.BeginTime != null;
                    bool hadEnd = item.EndTime != null;

                    if (!hadBegin && !hadEnd)
                    {
                        // 已经是"无时间窗"状态(本来就没有, 或我们已经清过)。
                        if (_cleared.Add(mapId)) LogInfo(mapId + " 极限档已可用(无时间窗)");
                        continue;
                    }

                    // 原窗口的秒数留到日志用; -1 表示该端本来就是空的。
                    long beforeBegin = hadBegin ? item.BeginTime.Seconds : -1L;
                    long beforeEnd = hadEnd ? item.EndTime.Seconds : -1L;

                    // ② 官方自己的覆盖入口:
                    //    beginTime_ = FixData.BeginTime; endTime_ = FixData.EndTime;
                    //    传一个空实例 => 两个字段都变 null => TimeHelper.ValidityTime 恒为真。
                    //    这一步必须发生在任何格式化/日志之前。
                    item.FixData(new FixMapMapLevelConfigureItem());

                    // ③ 读回自检 —— 不再靠推断说"应该生效了"。
                    bool ok = item.BeginTime == null && item.EndTime == null;

                    _cleared.Add(mapId);
                    LogInfo(mapId + " 已解除极限档时间窗(原 " + Fmt(beforeBegin) + " .. " + Fmt(beforeEnd)
                        + ") [" + reason + "] -> " + (ok ? "校验通过" : "!! 校验失败: 时间窗仍在"));
                }
            }

            if (!_announced && _cleared.Count >= ExtremeMaps.Length)
            {
                _announced = true;
                LogInfo("三张图的极限档时间窗均已解除, 房间设置里应当能看到「极限」了。");
            }
        }

        // =====================================================================
        // 时间格式化: 纯整数运算, 不碰任何 BCL 日期 API
        // =====================================================================

        /// <summary>
        /// unix 秒 → "yyyy-MM-dd HH:mmZ"(UTC)。
        ///
        /// <para>
        /// <b>刻意不用 <c>DateTime</c> / <c>DateTimeOffset</c>。</b>
        /// 实机日志证明 <c>System.DateTimeOffset::FromUnixTimeSeconds</c> 在 HybridCLR 下
        /// 是 <c>MethodNotFind</c>, 而且这类失败发生在方法被解析时, 同方法内的 try/catch 抓不住。
        /// 纯整数运算没有任何 AOT 元数据依赖, 从根上避免。
        /// </para>
        /// <para>算法: Howard Hinnant 的 civil_from_days(把"距 1970-01-01 的天数"换算成 年月日)。</para>
        /// </summary>
        private static string Fmt(long secs)
        {
            if (secs < 0) return "-";

            long days = secs / 86400L;
            long rem = secs % 86400L;
            long hh = rem / 3600L;
            long mm = (rem % 3600L) / 60L;

            long z = days + 719468L;
            long era = z / 146097L;
            long doe = z - era * 146097L;                              // [0, 146096]
            long yoe = (doe - doe / 1460L + doe / 36524L - doe / 146096L) / 365L;
            long y = yoe + era * 400L;
            long doy = doe - (365L * yoe + yoe / 4L - yoe / 100L);     // [0, 365]
            long mp = (5L * doy + 2L) / 153L;                          // [0, 11]
            long d = doy - (153L * mp + 2L) / 5L + 1L;                 // [1, 31]
            long mo = mp + (mp < 10L ? 3L : -9L);                      // [1, 12]
            if (mo <= 2L) y += 1L;

            return Pad(y, 4) + "-" + Pad(mo, 2) + "-" + Pad(d, 2)
                 + " " + Pad(hh, 2) + ":" + Pad(mm, 2) + "Z";
        }

        /// <summary>左侧补 0。只用到 long.ToString() 与 string.Length, 都是 AOT 必然存在的。</summary>
        private static string Pad(long v, int width)
        {
            string s = v.ToString();
            while (s.Length < width) s = "0" + s;
            return s;
        }

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

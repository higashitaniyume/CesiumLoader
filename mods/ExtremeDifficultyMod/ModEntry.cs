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
    public sealed partial class ExtremeDifficultyMod : ModBase
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

    }
}

// ExtraClockSources.cs - "界面倒计时"登记表(纯 BCL: 不引用 SDK / UnityEngine, 可直接编进测试工程)
//
// 变速 hook 的是引擎时钟, 所以除了出牌/商店那类 OperationTimer, 游戏里还有几处**界面自己的倒计时**
// 也会跟着加速, 到点后替玩家自动做动作:
//
//   - 选人界面: 选英雄到点自动确认, 选皮肤到点自动确认;
//   - 结算界面: 到点自动翻到下一页;
//   - 房间等待: 房主等待超时自动退出房间, 房主超时自动转让;
//   - 剧情窗口: 自动播放到点翻下一句。
//
// 这些 Timer 由各个界面/窗口自己持有, 字段名也各不相同, 所以这里按"运行时类型名 + 字段名"登记,
// 并支持字段改名后的兜底(该类型上所有 Timer 字段都算)。
//
// 只登记**到点会替玩家做事**的倒计时: 纯装饰性的计时器(提示窗自动关闭、表情残留、飘字)
// 故意不进这张表, 它们跟着变速走没有副作用。
using System;
using System.Collections.Generic;
using System.Reflection;

namespace TurnClockMod
{
    /// <summary>一处界面倒计时。</summary>
    internal sealed class ClockSource
    {
        internal ClockSource(string key, string label, string typeName, string[] fields, bool defaultOn, bool trace = true)
        {
            Key = key;
            Label = label;
            TypeName = typeName;
            Fields = fields ?? new string[0];
            DefaultOn = defaultOn;
            Trace = trace;
        }

        /// <summary>config.json 里的开关名。</summary>
        internal string Key { get; private set; }

        /// <summary>日志里显示的名字。</summary>
        internal string Label { get; private set; }

        /// <summary>计时器宿主类型名(不含命名空间, 例如 RoomHeroPanel)。</summary>
        internal string TypeName { get; private set; }

        /// <summary>计时器字段名, 按优先级排列。</summary>
        internal string[] Fields { get; private set; }

        /// <summary>默认是否开启。</summary>
        internal bool DefaultOn { get; private set; }

        /// <summary>是否在日志里逐个记录(剧情每个对话框都会换一个计时器, 关掉免刷屏)。</summary>
        internal bool Trace { get; private set; }
    }

    /// <summary>某个宿主类型上的一个计时器字段。</summary>
    internal sealed class TimerSlot
    {
        internal TimerSlot(ClockSource source, FieldInfo field, bool fallback)
        {
            Source = source;
            Field = field;
            FromFallback = fallback;
        }

        internal ClockSource Source { get; private set; }

        internal FieldInfo Field { get; private set; }

        /// <summary>字段名没对上, 按"类型是 Timer"兜底认领的。</summary>
        internal bool FromFallback { get; private set; }

        internal string Key { get { return Source != null ? Source.Key : ""; } }

        internal string Label { get { return Source != null ? Source.Label : ""; } }

        internal string FieldName { get { return Field != null ? Field.Name : ""; } }

        internal bool Trace { get { return Source != null && Source.Trace; } }

        /// <summary>读宿主上的计时器实例; 字段为 null 或读取失败时返回 null。</summary>
        internal object Read(object host)
        {
            if (host == null || Field == null) return null;
            try { return Field.GetValue(host); }
            catch { return null; }
        }
    }

    /// <summary>登记表: 运行时类型名 → 该类型上的计时器字段(按类型缓存探测结果)。</summary>
    internal sealed class ClockSourceTable
    {
        internal const BindingFlags InstanceAll =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>游戏计时器类型名(UnityTimer.Timer)。只认字段, 不认属性。</summary>
        internal const string TimerTypeName = "Timer";

        /// <summary>随包登记的界面倒计时。</summary>
        internal static readonly ClockSource[] Default =
        {
            new ClockSource("heroSelect", "选人倒计时", "RoomHeroPanel", new[] { "timeLimiter" }, true),
            new ClockSource("settlement", "结算倒计时", "BattleSettlementPanel", new[] { "operateTimer" }, true),
            new ClockSource("roomWait", "房间等待倒计时", "RoomWaitPanel", new[] { "roomTimer", "masterKickTimer" }, true),
            new ClockSource("storyAuto", "剧情自动播放", "StoryWindow", new[] { "AutoTimer" }, true, trace: false),
        };

        private readonly Dictionary<string, ClockSource> _byTypeName = new Dictionary<string, ClockSource>(StringComparer.Ordinal);
        private readonly Dictionary<Type, List<TimerSlot>> _resolved = new Dictionary<Type, List<TimerSlot>>();

        internal ClockSourceTable()
            : this(Default)
        {
        }

        internal ClockSourceTable(ClockSource[] sources)
        {
            if (sources == null) return;
            for (int i = 0; i < sources.Length; i++)
            {
                ClockSource source = sources[i];
                if (source == null || string.IsNullOrEmpty(source.TypeName)) continue;
                _byTypeName[source.TypeName] = source;
            }
        }

        /// <summary>按运行时类型名找登记项; 没登记返回 null。</summary>
        internal ClockSource Match(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            ClockSource source;
            return _byTypeName.TryGetValue(typeName, out source) ? source : null;
        }

        /// <summary>
        /// 解析宿主类型上的计时器字段(结果按类型缓存):
        /// 先按登记的名字找, 一个都没找到就认领该类型上所有 Timer 字段(游戏改名的兜底)。
        /// </summary>
        internal List<TimerSlot> Resolve(Type hostType)
        {
            if (hostType == null) return Empty;

            List<TimerSlot> slots;
            if (_resolved.TryGetValue(hostType, out slots)) return slots;

            ClockSource source = Match(hostType.Name);
            slots = new List<TimerSlot>();
            if (source != null)
            {
                string[] names = source.Fields;
                for (int i = 0; i < names.Length; i++)
                {
                    FieldInfo field = FindTimerField(hostType, names[i]);
                    if (field != null) slots.Add(new TimerSlot(source, field, false));
                }

                if (slots.Count == 0) CollectFallback(hostType, source, slots);
            }

            _resolved[hostType] = slots;
            return slots;
        }

        /// <summary>字段是不是游戏计时器(UnityTimer.Timer)。</summary>
        internal static bool IsTimerField(FieldInfo field)
        {
            if (field == null) return false;
            try { return field.FieldType != null && field.FieldType.Name == TimerTypeName; }
            catch { return false; }
        }

        private static FieldInfo FindTimerField(Type hostType, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                FieldInfo field = hostType.GetField(name, InstanceAll);
                return IsTimerField(field) ? field : null;
            }
            catch { return null; }
        }

        private static void CollectFallback(Type hostType, ClockSource source, List<TimerSlot> into)
        {
            FieldInfo[] fields;
            try { fields = hostType.GetFields(InstanceAll); }
            catch { return; }

            var found = new List<FieldInfo>();
            for (int i = 0; i < fields.Length; i++)
                if (IsTimerField(fields[i])) found.Add(fields[i]);

            // GetFields 的顺序没有保证; 按名字排一下, 让同一次运行里两次探测的结果一致
            found.Sort(CompareByName);
            for (int i = 0; i < found.Count; i++) into.Add(new TimerSlot(source, found[i], true));
        }

        private static int CompareByName(FieldInfo left, FieldInfo right)
        {
            string a = left != null ? left.Name : "";
            string b = right != null ? right.Name : "";
            return string.CompareOrdinal(a, b);
        }

        private static readonly List<TimerSlot> Empty = new List<TimerSlot>();
    }
}

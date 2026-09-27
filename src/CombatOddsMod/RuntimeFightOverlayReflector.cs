using System;
using CesiumLoader.SDK;
using Tools;   // SimpleSingletonProvider<>
using UI;      // UIManager / FightWindow
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 真实 FairyGUI 反射实现: 通过 SimpleSingletonProvider&lt;UIManager&gt;.Fight 取窗口,
    /// 用 RuntimeAssemblyService 反射访问 FairyGUI 成员(不需要编译期引用 FairyGUI)。
    ///
    /// 注意: 本类无法离线验证——真机进游戏才能目视确认标签是否出现在 FightWindow 上。
    /// 所有操作全部包 try/catch, 任一步失败都降级(返回 null / no-op), 由控制器隐藏并让
    /// ModEntry 的控制台/通知输出兜底, 绝不因 UI 反射失败影响计算与日志。
    /// </summary>
    public sealed class RuntimeFightOverlayReflector : IFightOverlayReflector
    {
        // FightWindow 实例(强类型可取, 但其 FairyGUI 成员一律反射)。
        private object GetFightWindow()
        {
            try { return UIManager.inst?.Fight; }
            catch { return null; }
        }

        public bool IsFightShowing()
        {
            var win = GetFightWindow();
            if (win == null) return false;
            try { return RuntimeAssemblyService.SafeGetProperty<bool>(win, "isShowing", false, "CombatOdds.overlay"); }
            catch { return false; }
        }

        public object GetFightPane()
        {
            var win = GetFightWindow();
            if (win == null) return null;
            try { return RuntimeAssemblyService.SafeGetProperty(win, "contentPane", "CombatOdds.overlay"); }
            catch { return null; }
        }

        public object CreateLabel()
        {
            try
            {
                var gtfType = RuntimeAssemblyService.FindTypeBySimpleName("GTextField");
                if (gtfType == null) return null;
                var label = Activator.CreateInstance(gtfType);
                if (label == null) return null;

                // 尺寸/位置: 摆在窗口左上, 足够放结论 + 全体攻防面板(最多 ~10 行)。
                RuntimeAssemblyService.SafeInvoke(label, "SetSize", new object[] { 600f, 480f }, "CombatOdds.overlay");
                RuntimeAssemblyService.SafeInvoke(label, "SetXY", new object[] { 24f, 24f }, "CombatOdds.overlay");
                // 兼容 FairyGUI 不同版本的定位方法名。
                RuntimeAssemblyService.SafeInvoke(label, "SetPosition", new object[] { 24f, 24f, 0f }, "CombatOdds.overlay");

                RuntimeAssemblyService.SafeSetProperty(label, "touchable", false, "CombatOdds.overlay");
                RuntimeAssemblyService.SafeSetProperty(label, "sortingOrder", 9000, "CombatOdds.overlay");
                // 开启 UBB, 让 [color]/[size] 富文本标签被 UBBParser 解析(否则会原样显示 "[color...]")。
                // 注意: 属性名是 UBBEnabled(大写 UBB); 同时直接写 protected 字段 _ubbEnabled 兜底。
                RuntimeAssemblyService.SafeSetProperty(label, "UBBEnabled", true, "CombatOdds.overlay");
                RuntimeAssemblyService.SafeSetField(label, "_ubbEnabled", true, "CombatOdds.overlay");
                bool ubbOk = RuntimeAssemblyService.SafeGetProperty<bool>(label, "UBBEnabled", false, "CombatOdds.overlay");
                _ubbOk = ubbOk;
                SdkLog.Info("CombatOdds", "[overlay] UBBEnabled=" + ubbOk + " (true 才会渲染彩色, 否则自动降级纯文本)");

                // 文本格式: 白字加粗描边, 保证深色战斗背景上可读。
                var tf = RuntimeAssemblyService.SafeGetProperty(label, "textFormat", "CombatOdds.overlay");
                if (tf != null)
                {
                    RuntimeAssemblyService.SafeSetField(tf, "size", 22, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeSetProperty(tf, "size", 22, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeSetField(tf, "color", Color.white, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeSetProperty(tf, "color", Color.white, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeSetField(tf, "bold", true, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeSetField(tf, "outline", 2f, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeSetField(tf, "outlineColor", Color.black, "CombatOdds.overlay");
                    RuntimeAssemblyService.SafeInvoke(label, "ApplyFormat", null, "CombatOdds.overlay");
                    // 有的版本用 textFormat setter 生效。
                    RuntimeAssemblyService.SafeSetProperty(label, "textFormat", tf, "CombatOdds.overlay");
                }

                SdkLog.Info("CombatOdds", "已创建 FightWindow 覆盖层标签(等待进游戏目视确认位置)");
                return label;
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "创建覆盖层标签失败(降级到控制台): " + e.Message);
                return null;
            }
        }

        public bool LabelInPane(object label, object pane)
        {
            if (label == null || pane == null) return false;
            try
            {
                var parent = RuntimeAssemblyService.SafeGetProperty(label, "parent", "CombatOdds.overlay");
                return ReferenceEquals(parent, pane);
            }
            catch { return false; }
        }

        public void AddChildToPane(object pane, object label)
        {
            if (pane == null || label == null) return;
            try
            {
                RuntimeAssemblyService.SafeInvoke(pane, "AddChild", new object[] { label }, "CombatOdds.overlay");
                SdkLog.Info("CombatOdds", "[overlay] AddChild → paneId=" + Id(pane) + " labelId=" + Id(label));
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "覆盖层挂载失败: " + e.Message); }
        }

        public void SetText(object label, string text)
        {
            if (label == null) return;
            try
            {
                // UBB 未成功开启时(极少数 FairyGUI 变体), 主动去掉标签, 绝不让 "[color...]" 原样显示。
                string payload = _ubbOk ? (text ?? string.Empty) : StripUbb(text ?? string.Empty);
                RuntimeAssemblyService.SafeSetProperty(label, "text", payload, "CombatOdds.overlay");
                // 诊断: 仅在"是否还挂在容器上/可见性"变化时打一行(不再每 tick 刷屏, 避免日志噪声与卡顿)。
                var parent = RuntimeAssemblyService.SafeGetProperty(label, "parent", "CombatOdds.overlay");
                var vis = RuntimeAssemblyService.SafeGetProperty<bool>(label, "visible", false, "CombatOdds.overlay");
                string sig = Id(parent) + "/" + vis;
                if (sig != _lastDiag)
                {
                    _lastDiag = sig;
                    SdkLog.Info("CombatOdds", "[overlay] labelId=" + Id(label) + " parentId=" + Id(parent) + " visible=" + vis);
                }
            }
            catch { /* 降级: 控制台已输出 */ }
        }

        private bool _ubbOk;
        private string _lastDiag;

        private static readonly System.Text.RegularExpressions.Regex _ubbRe =
            new System.Text.RegularExpressions.Regex(@"\[/?(color|size|b|i|u)(=[^\]]*)?\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static string StripUbb(string s) =>
            string.IsNullOrEmpty(s) ? s : _ubbRe.Replace(s, string.Empty);

        public void SetVisible(object label, bool visible)
        {
            if (label == null) return;
            try { RuntimeAssemblyService.SafeSetProperty(label, "visible", visible, "CombatOdds.overlay"); }
            catch { }
        }

        // 稳定的对象身份(不持有引用), 便于在日志里判断"标签是否还是原来那个/还挂在原容器上"。
        private static string Id(object o)
        {
            if (o == null) return "null";
            try { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString("x"); }
            catch { return "?"; }
        }
    }
}

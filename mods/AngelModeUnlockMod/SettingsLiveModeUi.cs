using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Logging;
using Core;
using FairyGUI;
using Tools;
using UI;

namespace AngelModeUnlockMod
{
    /// <summary>
    /// 设置页「功能」标签里的天使模式开关行。
    ///
    /// 审核渠道用的 FairyGUI 包是删减版: 绑定代码(<c>UISetting_Com_LiveMode</c>)是同一份热更代码,
    /// 靠 <c>GetChildAt(3)</c> 这种固定下标取控件, 所以包里的行只能"留着但藏起来"——
    /// 做法是把这一行(底板 + 开关)编进一个隐藏的 <c>GGroup</c>, 或者编进 <c>isAuditMode</c>
    /// 控制器的审核页。<c>LanguageUnlockMod</c> 在同一个包的语言行上已经踩过: 语言行被编进隐藏组
    /// <c>n49</c>, 把同组成员从组里摘出来、恢复 visible/alpha 就能看见。
    ///
    /// 本类做两件事, 都是每帧幂等:
    /// 1. 首次拿到设置窗口时把「功能」页的真实结构打进日志(子控件名字/类型/坐标/可见性/所属组、
    ///    控制器页数、页签名), 用来判断到底是"控件不存在"还是"存在但被藏起来"。
    /// 2. <c>restoreSwitch</c> 打开时把开关行从隐藏组里摘出来并恢复可见可点, 同时把
    ///    <c>isAuditMode</c> 控制器压到非审核页(1)。控制器页在每次切页签时会被重新 Apply,
    ///    所以每帧复核一次, 而不是只在窗口打开时做一次。
    /// </summary>
    internal sealed class SettingsLiveModeUi
    {
        // UIManager 只在玩家真的点开设置时才创建窗口(property setting 里 new), 不能为了读结构
        // 主动去碰 setting 属性 —— 那时 FairyGUI 的 Setting 包可能还没异步加载好, 会造出半个窗口。
        // 和 LanguageUnlockMod 一样只读缓存字段, 窗口不存在就等它被创建。
        private static readonly FieldInfo CachedWindowField =
            typeof(UIManager).GetField("_cachedSettingWindow", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly ModLogger _log;

        private SettingWindow _window;
        private UISetting_Com_LiveMode _liveMode;
        private int _dumpStage = -1;
        private bool _groupDetached;
        private bool _titleFixed;
        private bool _missingToggleLogged;
        private int _lastForcedAuditIndex = int.MinValue;
        private bool _lastSelectedSync = true;
        private bool _selectedSyncAttempted;
        private DateTime _nextSelectedSyncUtc = DateTime.MinValue;
        private DateTime _nextErrorLogUtc = DateTime.MinValue;
        private readonly List<GObject> _rowObjects = new List<GObject>();

        internal SettingsLiveModeUi(ModLogger log)
        {
            _log = log;
        }

        /// <summary>每帧调用。auditMode 为当前(可能已被本模组清成 false 的)RemoteConfig 审核标记。</summary>
        internal void Tick(bool auditMode, bool restoreSwitch, string switchTitle)
        {
            try
            {
                TickCore(auditMode, restoreSwitch, switchTitle);
            }
            catch (Exception e)
            {
                // 每帧调用, 出错时不能每帧刷一行(会把日志冲掉); 同类错误 10 秒报一次
                if (DateTime.UtcNow < _nextErrorLogUtc) return;
                _nextErrorLogUtc = DateTime.UtcNow.AddSeconds(10);
                _log.Warn("设置页天使模式开关同步失败: " + e.Message);
            }
        }

        private void TickCore(bool auditMode, bool restoreSwitch, string switchTitle)
        {
            SettingWindow window = GetWindow();
            if (window == null)
            {
                Reset();
                return;
            }

            GComponent pane;
            try { pane = window.contentPane; } catch { pane = null; }
            UISettingWindow ui = pane as UISettingWindow;
            if (ui == null)
            {
                Reset();
                return;
            }

            UISetting_Com_LiveMode liveMode = ui.com_LiveMode;
            if (liveMode == null)
            {
                if (!_missingToggleLogged)
                {
                    _missingToggleLogged = true;
                    _log.Warn("设置窗口里没有 com_LiveMode（Setting 包结构与热更绑定不符），无法恢复天使模式开关。");
                }
                return;
            }

            if (!ReferenceEquals(_window, window) || !ReferenceEquals(_liveMode, liveMode))
            {
                Reset();
                _window = window;
                _liveMode = liveMode;
            }

            // 分三级打点: 窗口刚创建 / 窗口显示 / 玩家切到「功能」页。
            // 行被藏起来可能发生在其中任一级(控制器在窗口显示时 Apply, 组在切页时 Apply),
            // 三次快照对比才能定位到底哪一步把它收起来。
            bool showing = SafeShowing(window);
            int stage = 0;
            if (liveMode.visible) stage = 2;
            else if (showing) stage = 1;
            if (stage > _dumpStage)
            {
                _dumpStage = stage;
                DumpLayout(ui, liveMode, auditMode,
                    stage == 0 ? "窗口已创建" : (stage == 1 ? "窗口已显示" : "功能页已激活"));
            }

            if (!restoreSwitch) return;

            ForceAuditPage(liveMode, auditMode);
            RestoreToggle(liveMode, switchTitle);
            KeepRowVisible();
        }

        private static SettingWindow GetWindow()
        {
            try
            {
                if (CachedWindowField == null) return null;
                if (!SimpleSingletonProvider<UIManager>.hasInstance) return null;
                object cached = CachedWindowField.GetValue(SimpleSingletonProvider<UIManager>.inst);
                return cached as SettingWindow;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 恢复
        // ------------------------------------------------------------------

        /// <summary>把 isAuditMode 控制器压到非审核页(1)。审核页(0)是删减版包藏开关的第二种方式。</summary>
        private void ForceAuditPage(UISetting_Com_LiveMode liveMode, bool auditMode)
        {
            Controller controller = liveMode.isAuditMode;
            if (controller == null) return;

            int want = auditMode ? 0 : 1;
            if (controller.pageCount <= want) return;
            if (controller.selectedIndex == want) return;

            controller.selectedIndex = want;
            if (_lastForcedAuditIndex != want)
            {
                _lastForcedAuditIndex = want;
                _log.Info("功能页 isAuditMode 控制器 → " + want + "/" + (controller.pageCount - 1)
                    + "（" + (auditMode ? "审核页" : "非审核页") + "）");
            }
        }

        private void RestoreToggle(UISetting_Com_LiveMode liveMode, string switchTitle)
        {
            UISetting_Com_Toggle toggle = liveMode.com_AdultSwither;
            if (toggle == null)
            {
                if (!_missingToggleLogged)
                {
                    _missingToggleLogged = true;
                    _log.Warn("功能页里没有 com_AdultSwither，删减版包可能直接删掉了开关控件。");
                }
                return;
            }

            DetachFromHiddenGroup(toggle);

            if (!toggle.visible) toggle.visible = true;
            if (toggle.alpha < 1f) toggle.alpha = 1f;
            if (!toggle.touchable) toggle.touchable = true;
            if (toggle.grayed) toggle.grayed = false;

            // 删减版包把这一行做成了「自由镜头」的翻版(连文案都照抄), 只说"标题为空"不够:
            // 从隐藏组里摘出来之后统一改成天使模式, 否则设置页会出现两个一模一样的「自由镜头」。
            if (!_titleFixed && (_groupDetached || IsBlank(SafeTitle(toggle))))
            {
                _titleFixed = true;
                string desired = ResolveTitle(switchTitle);
                string current = SafeTitle(toggle);
                if (!string.Equals(current, desired, StringComparison.Ordinal))
                {
                    try
                    {
                        toggle.title = desired;
                        _log.Info("天使模式开关标题 '" + (current ?? "") + "' → '" + desired + "'。");
                    }
                    catch (Exception e)
                    {
                        _log.Warn("修正开关标题失败: " + e.Message);
                    }
                }
            }

            // 状态对齐走游戏自己的字段, 不额外发 RPC: selected 的 onChanged 是 SettingWindow.OnAdultSwitch,
            // 它只在 selected != GameSettings.angelMode 时才动, 先写字段就不会触发多余的一次同步。
            bool selected;
            try { selected = toggle.selected; } catch { return; }
            if (selected != GameSettings.angelMode
                && (!_selectedSyncAttempted || _lastSelectedSync != GameSettings.angelMode)
                && DateTime.UtcNow >= _nextSelectedSyncUtc)
            {
                _selectedSyncAttempted = true;
                _lastSelectedSync = GameSettings.angelMode;
                _nextSelectedSyncUtc = DateTime.UtcNow.AddSeconds(2);
                try { toggle.selected = GameSettings.angelMode; }
                catch (Exception e) { _log.Debug("同步开关状态失败: " + e.Message); }
            }
        }

        /// <summary>
        /// 把目标所在的行(同组兄弟一起)从隐藏的 GGroup 里摘出来。只摘目标会留下"有开关没底板"。
        /// 与 LanguageUnlockMod 的做法一致: RemoveChild 会把 child.group 置 null, 再 AddChild 挂回父级。
        /// </summary>
        private void DetachFromHiddenGroup(UISetting_Com_Toggle toggle)
        {
            if (_groupDetached) return;

            GComponent parent = toggle.parent;
            GGroup group = toggle.group;
            if (parent == null || group == null) return;

            var siblings = new List<GObject>();
            for (int i = 0; i < parent.numChildren; i++)
            {
                GObject child = parent.GetChildAt(i);
                if (child != null && ReferenceEquals(child.group, group)) siblings.Add(child);
            }

            for (int i = 0; i < siblings.Count; i++)
            {
                GObject child = siblings[i];
                if (child == null || child.isDisposed) continue;
                parent.RemoveChild(child);
                parent.AddChild(child);
                child.visible = true;
                if (child.alpha < 1f) child.alpha = 1f;
            }

            _groupDetached = true;
            _rowObjects.Clear();
            _rowObjects.AddRange(siblings);
            _log.Info("天使模式开关行已从隐藏分组 " + (group.name ?? "(null)") + " 移出, 对象数 " + siblings.Count);
        }

        /// <summary>删减版包会在切页签时用控制器/组把行重新藏起来, 每帧复核一次。</summary>
        private void KeepRowVisible()
        {
            for (int i = 0; i < _rowObjects.Count; i++)
            {
                GObject child = _rowObjects[i];
                if (child == null || child.isDisposed) continue;
                if (!child.visible) child.visible = true;
                if (child.alpha < 1f) child.alpha = 1f;
            }
        }

        private static string SafeTitle(UISetting_Com_Toggle toggle)
        {
            try { return toggle.title; } catch { return null; }
        }

        private static bool IsBlank(string text)
        {
            return text == null || text.Trim().Length == 0;
        }

        private static string ResolveTitle(string configured)
        {
            if (!string.IsNullOrEmpty(configured)) return configured;
            try
            {
                switch (GameSettings.GetLanguagesType())
                {
                    case LanguageType.TraditionalChinese: return "天使模式";
                    case LanguageType.English: return "Angel Mode";
                    case LanguageType.Japanese: return "天使モード";
                    case LanguageType.Korean: return "엔젤 모드";
                    default: return "天使模式";
                }
            }
            catch
            {
                return "天使模式";
            }
        }

        // ------------------------------------------------------------------
        // 结构诊断(只打一次)
        // ------------------------------------------------------------------

        private void DumpLayout(UISettingWindow ui, UISetting_Com_LiveMode liveMode, bool auditMode, string stage)
        {
            try
            {
                _log.Info("[UI] 设置窗口: children=" + ui.numChildren
                    + ", " + DescribeController("tab", ui.tab)
                    + ", " + DescribeController("country", ui.country)
                    + ", " + DescribeController("isPC", ui.isPC));

                _log.Info("[UI] 功能页: children=" + liveMode.numChildren
                    + ", " + DescribeController("isAuditMode", liveMode.isAuditMode)
                    + ", 快照=" + stage
                    + ", RemoteConfig.IsAuditMode=" + auditMode
                    + ", GameSettings.angelMode=" + GameSettings.angelMode);

                for (int i = 0; i < liveMode.numChildren; i++)
                {
                    _log.Info("[UI]   " + DescribeObject(liveMode.GetChildAt(i), i));
                }

                DumpTabs();
            }
            catch (Exception e)
            {
                _log.Warn("[UI] 设置页结构诊断失败: " + e.Message);
            }
        }

        private void DumpTabs()
        {
            try
            {
                SettingsConfigure settings = StaticConfigure.Settings;
                if (settings == null)
                {
                    _log.Info("[UI] 页签配置未就绪");
                    return;
                }
                var list = settings.SettingsTabs;
                if (list == null)
                {
                    _log.Info("[UI] 页签配置为空");
                    return;
                }
                var text = new StringBuilder();
                text.Append("[UI] 页签配置 ").Append(list.Count).Append(" 项:");
                for (int i = 0; i < list.Count; i++)
                {
                    SettingsSettingsTabConfigure tab = list[i];
                    if (tab == null) continue;
                    text.Append(' ').Append(i).Append(':').Append(tab.SettingTabType)
                        .Append("/order=").Append(tab.ShowOrder)
                        .Append("/onlyPC=").Append(tab.IsOnlyPCSetting);
                }
                _log.Info(text.ToString());
            }
            catch (Exception e)
            {
                _log.Debug("读取页签配置失败: " + e.Message);
            }
        }

        private static bool SafeShowing(SettingWindow window)
        {
            try
            {
                return window != null && window.isShowing;
            }
            catch { return false; }
        }

        private static string DescribeController(string label, Controller controller)
        {
            if (controller == null) return label + "=null";
            var text = new StringBuilder();
            text.Append(label).Append('=').Append(controller.name ?? "(null)")
                .Append('(').Append(controller.selectedIndex).Append('/').Append(controller.pageCount - 1).Append(')');
            if (controller.pageCount > 0)
            {
                text.Append(" pages=");
                for (int i = 0; i < controller.pageCount; i++)
                {
                    if (i > 0) text.Append(',');
                    string name;
                    try { name = controller.GetPageName(i) ?? controller.GetPageId(i); }
                    catch { name = "?"; }
                    text.Append(name ?? "?");
                }
            }
            return text.ToString();
        }

        private static string DescribeObject(GObject o, int index)
        {
            if (o == null) return "child[" + index + "]=null";
            var text = new StringBuilder();
            text.Append("child[").Append(index).Append("] ")
                .Append(o.name ?? "(null)").Append(' ').Append(o.GetType().Name)
                .Append(" xy=").Append(o.x).Append(',').Append(o.y)
                .Append(" size=").Append(o.width).Append('x').Append(o.height)
                .Append(" visible=").Append(o.visible)
                .Append(" alpha=").Append(o.alpha)
                .Append(" touchable=").Append(o.touchable)
                .Append(" grayed=").Append(o.grayed)
                .Append(" group=").Append(o.group == null ? "(null)" : (o.group.name ?? "(null)"));

            GButton button = o as GButton;
            if (button != null)
            {
                try { text.Append(" title='").Append(button.title ?? "").Append('\''); }
                catch { }
                try { text.Append(" selected=").Append(button.selected); }
                catch { }
            }
            GLoader loader = o as GLoader;
            if (loader != null)
            {
                try { text.Append(" url=").Append(loader.url ?? ""); }
                catch { }
            }
            return text.ToString();
        }

        private void Reset()
        {
            _window = null;
            _liveMode = null;
            _dumpStage = -1;
            _groupDetached = false;
            _titleFixed = false;
            _selectedSyncAttempted = false;
            _rowObjects.Clear();
        }
    }
}


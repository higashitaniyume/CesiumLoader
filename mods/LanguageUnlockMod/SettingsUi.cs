using System;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using Cysharp.Threading.Tasks;
using Core;
using FairyGUI;
using Tools;
using UI;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 接管「设置 → 屏幕 → 语言」下拉框。
    ///
    /// 国服 <c>GameSettings.Update_LanguagesType</c> 是空函数，游戏自己的 onChanged 回调会弹确认框、
    /// 调一次空函数、然后重登，语言纹丝不动。这里在窗口显示时把游戏的监听清掉，换成自己的：
    /// 确认后切换语言并重登。FairyGUI 的 <c>EventListener.Clear()</c> 足够用，不需要 Harmony。
    ///
    /// 另外游戏每次刷新界面都会把下拉框刷回简中（国服 <c>GetLanguagesType()</c> 恒返简中），
    /// 所以每帧把选中项校正回当前语言。
    /// </summary>
    internal sealed class SettingsUi
    {
        private static readonly FieldInfo CachedWindowField =
            typeof(UIManager).GetField("_cachedSettingWindow", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly LanguageController _owner;
        private readonly ModLogger _log;

        private SettingWindow _window;
        private GComboBox _combo;
        private bool _hooked;
        private int _suppress;
        private int _framesSinceHook;
        private DateTime _dialogUntil = DateTime.MinValue;
        private bool _loggedLayout;
        private readonly System.Collections.Generic.List<GObject> _rowObjects = new System.Collections.Generic.List<GObject>();

        internal SettingsUi(LanguageController owner, ModLogger log)
        {
            _owner = owner;
            _log = log;
        }

        internal void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception e)
            {
                _log.Warn("语言下拉框同步失败: " + e.Message);
            }
        }

        internal void Release()
        {
            try
            {
                if (_combo != null) _combo.onChanged.Remove(new EventCallback1(OnChanged));
            }
            catch { }
            Reset();
        }

        private void TickCore()
        {
            SettingWindow window = GetWindow();
            if (window == null) { Reset(); return; }

            bool showing;
            try { showing = window.isShowing; } catch { showing = false; }
            if (!showing) { Reset(); return; }

            GComponent pane;
            try { pane = window.contentPane; } catch { pane = null; }
            UISettingWindow ui = pane as UISettingWindow;
            if (ui == null) { Reset(); return; }

            UISetting_Com_Screen screen = ui.com_Screen;
            if (screen == null) { Reset(); return; }

            GComboBox combo = screen.com_Language;
            if (combo == null) { Reset(); return; }

            if (!_hooked || !ReferenceEquals(_window, window) || !ReferenceEquals(_combo, combo))
            {
                Hook(combo);
                _window = window;
                _combo = combo;
                _hooked = true;
                _framesSinceHook = 0;
                ForceVisible(combo);
                if (!_loggedLayout)
                {
                    _loggedLayout = true;
                    DumpLayout(screen, combo);
                }
            }
            else if (++_framesSinceHook >= 30)
            {
                // 游戏每次 OnShown 都会把 ChangeLanguage 重新挂回来，定期清一次保证只有我们的回调。
                // Clear + Add 在同一帧同步完成，不会丢掉玩家的选择事件。
                Hook(combo);
                _framesSinceHook = 0;
            }

            if (!combo.visible) combo.visible = true;
            KeepRowVisible();
            Sync(combo);
        }

        private static SettingWindow GetWindow()
        {
            try
            {
                if (CachedWindowField == null) return null;
                object cached = CachedWindowField.GetValue(SimpleSingletonProvider<UIManager>.inst);
                return cached as SettingWindow;
            }
            catch
            {
                return null;
            }
        }

        private void Hook(GComboBox combo)
        {
            combo.onChanged.Clear();
            combo.onChanged.Add(new EventCallback1(OnChanged));
        }

        private void OnChanged(EventContext context)
        {
            if (_suppress > 0) return;

            GComboBox combo = _combo;
            if (combo == null) return;

            SettingsConfigure settings = StaticConfigure.Settings;
            if (settings == null) return;

            int index = combo.selectedIndex;
            if (index < 0 || index >= settings.Languages.Count) return;

            LanguageType target = settings.Languages[index].LanguageType;
            if (target == _owner.Selected)
            {
                Sync(combo);
                return;
            }

            try
            {
                // 确认框开着的时候不要再把下拉框刷回原语言, 否则玩家会看到选中项跳回去。
                _dialogUntil = DateTime.UtcNow.AddSeconds(30);
                SimpleSingletonProvider<UIManager>.inst.messageBox.ShowOKCancel(
                    34,
                    () => { _dialogUntil = DateTime.MinValue; _owner.RequestSwitch(target); },
                    () => { _dialogUntil = DateTime.MinValue; Sync(combo); }).Forget();
            }
            catch (Exception e)
            {
                _dialogUntil = DateTime.MinValue;
                _log.Warn("打开语言确认框失败: " + e.Message);
                Sync(combo);
            }
        }

        private void Sync(GComboBox combo)
        {
            if (DateTime.UtcNow < _dialogUntil) return;

            SettingsConfigure settings = StaticConfigure.Settings;
            if (settings == null) return;

            int index = IndexOf(settings, _owner.Selected);
            if (index < 0) return;

            string[] items = combo.items;
            if (items == null || items.Length != settings.Languages.Count)
            {
                var labels = new string[settings.Languages.Count];
                for (int i = 0; i < labels.Length; i++) labels[i] = LanguageRuntime.Label(settings.Languages[i]);

                _suppress++;
                try { combo.items = labels; }
                finally { _suppress--; }
            }

            string label = LanguageRuntime.Label(settings.Languages[index]);
            GTextField text = combo.GetTextField();
            if (combo.selectedIndex == index && (text == null || text.text == label)) return;

            _suppress++;
            try
            {
                combo.selectedIndex = index;
                if (text != null) text.text = label;
            }
            finally
            {
                _suppress--;
            }
        }

        private static int IndexOf(SettingsConfigure settings, LanguageType language)
        {
            var list = settings.Languages;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].LanguageType == language) return i;
            }
            return -1;
        }

        private void ForceVisible(GComboBox combo)
        {
            try
            {
                GComponent parent = combo.parent;
                GGroup group = combo.group;
                if (parent != null && group != null)
                {
                    // 语言这一行(标题底板 + 下拉框)被编在同一个 GGroup 里, 国服的 country 控制器
                    // 把整组隐藏。只把下拉框露出来会出现"有框没标题", 所以把同组的兄弟对象一起
                    // 从组里移出来, 并逐个恢复 visible/alpha。
                    var siblings = new System.Collections.Generic.List<GObject>();
                    for (int i = 0; i < parent.numChildren; i++)
                    {
                        GObject child = parent.GetChildAt(i);
                        if (child != null && ReferenceEquals(child.group, group)) siblings.Add(child);
                    }

                    for (int i = 0; i < siblings.Count; i++)
                    {
                        GObject child = siblings[i];
                        parent.RemoveChild(child);
                        parent.AddChild(child);
                        child.visible = true;
                        child.alpha = 1f;
                    }

                    _rowObjects.Clear();
                    _rowObjects.AddRange(siblings);
                    _log.Info("语言行已从隐藏分组 " + (group.name ?? "(null)") + " 移出, 对象数 " + siblings.Count);
                }

                combo.visible = true;
                combo.alpha = 1f;
                combo.touchable = true;
                if (parent != null && parent.numChildren > 1)
                {
                    parent.SetChildIndex(combo, parent.numChildren - 1);
                }
            }
            catch (Exception e)
            {
                _log.Warn("调整语言控件失败: " + e.Message);
            }
        }

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

        private void Reset()
        {
            _hooked = false;
            _window = null;
            _combo = null;
            _rowObjects.Clear();
        }

        /// <summary>只打一次: 记录语言控件在画面页里的真实布局, 便于判断它为什么看不见。</summary>
        private void DumpLayout(UISetting_Com_Screen screen, GComboBox combo)
        {
            try
            {
                var text = new System.Text.StringBuilder();
                text.Append("画面页布局: languages=").Append(StaticConfigure.Settings == null ? -1 : StaticConfigure.Settings.Languages.Count);
                text.Append(", current=").Append(Core.GameSettings.GetLanguagesType());
                text.Append(", screen=").Append(screen.width).Append('x').Append(screen.height).Append(" children=").Append(screen.numChildren);
                Controller country = screen.country;
                if (country != null) text.Append(", country=").Append(country.selectedIndex).Append('/').Append(country.pageCount);
                _log.Info(text.ToString());

                if (combo != null)
                {
                    _log.Info("com_Language: name=" + (combo.name ?? "(null)")
                              + " xy=" + combo.x + "," + combo.y
                              + " size=" + combo.width + "x" + combo.height
                              + " visible=" + combo.visible + " alpha=" + combo.alpha
                              + " touchable=" + combo.touchable
                              + " parent=" + (combo.parent == null ? "(null)" : combo.parent.name)
                              + " group=" + (combo.group == null ? "(null)" : combo.group.name)
                              + " index=" + (combo.parent == null ? -1 : combo.parent.GetChildIndex(combo))
                              + " sort=" + combo.sortingOrder
                              + " items=" + (combo.items == null ? -1 : combo.items.Length));
                }

                for (int i = 0; i < screen.numChildren; i++)
                {
                    GObject child = screen.GetChildAt(i);
                    if (child == null) continue;
                    _log.Info("  child[" + i + "] " + (child.name ?? "(null)")
                              + " xy=" + child.x + "," + child.y
                              + " size=" + child.width + "x" + child.height
                              + " visible=" + child.visible + " alpha=" + child.alpha
                              + " group=" + (child.group == null ? "(null)" : child.group.name));
                }
            }
            catch (Exception e)
            {
                _log.Warn("布局诊断失败: " + e.Message);
            }
        }
    }
}










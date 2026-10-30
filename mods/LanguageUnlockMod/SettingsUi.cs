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
            }
            else if (++_framesSinceHook >= 30)
            {
                // 游戏每次 OnShown 都会把 ChangeLanguage 重新挂回来，定期清一次保证只有我们的回调。
                // Clear + Add 在同一帧同步完成，不会丢掉玩家的选择事件。
                Hook(combo);
                _framesSinceHook = 0;
            }

            if (!combo.visible) combo.visible = true;
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

        private void Reset()
        {
            _hooked = false;
            _window = null;
            _combo = null;
        }
    }
}


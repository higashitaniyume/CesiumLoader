using System;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.UserInterface;
using UnityEngine;

namespace HideHudMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new HideHudController(), tag: "HideHud");
        }
    }

    /// <summary>
    /// 战斗 HUD 隐藏器。
    ///
    /// 默认长按 `（Tab 上方）隐藏，松开恢复；配置 pressMode=Toggle 后改为按一下切换。
    /// 只改游戏 UI 的可见性，不拦截输入、不改对局数据。
    /// </summary>
    public sealed class HideHudController : ModBase
    {
        private HideHudSettings _settings = new HideHudSettings();
        private readonly HudHotkeyState _hotkey = new HudHotkeyState();
        private readonly HudHider _hider = new HudHider();

        private bool _applied;
        private float _nextErrorLog;
        private string _lastError;

        public override string Name { get { return "隐藏HUD"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get
            {
                return "长按反引号键（Tab 上方）隐藏战斗 HUD，松开恢复；可改为按一下切换。"
                    + "手牌、怪物信息、头像与其他 HUD 可分别开关。";
            }
        }

        public override void OnInitialize()
        {
            HideHudSettings.EnsureDefaults(Config);
            _settings = HideHudSettings.Read(Config, warning => Log.Warn(warning));

            Log.Info("隐藏HUD: " + DescribeSettings());
            if (!_settings.Enabled)
            {
                Log.Info("隐藏HUD 处于禁用状态（enabled=false），不会响应热键。");
                return;
            }

            if (_settings.Mode == HudPressMode.Hold)
                Log.Info("长按 " + _settings.Key + " " + _settings.HoldSeconds.ToString("0.00")
                    + " 秒隐藏，松开恢复；pressMode 改成 Toggle 可改为按一下切换。");
            else
                Log.Info("按一下 " + _settings.Key + " 切换隐藏/恢复。");
        }

        public override void OnConfigChanged()
        {
            HideHudSettings old = _settings;
            _settings = HideHudSettings.Read(Config, warning => Log.Warn(warning));

            bool bindChanged = old.Key != _settings.Key || old.Mode != _settings.Mode;
            if (!_settings.Enabled || bindChanged)
            {
                // 改键/改模式时不保留旧状态，避免“按着旧键隐藏、新键却恢复不了”。
                _hider.Clear();
                _hotkey.Reset(InputService.IsKeyHeld(_settings.Key), UnityTime.RealtimeSinceStartup);
                _applied = false;
            }
            else if (_hotkey.Hidden)
            {
                _hider.Apply(_settings, true);
            }

            if (!_settings.Enabled)
                Log.Info("隐藏HUD 已禁用（enabled=false）");
            else
                Log.Info("隐藏HUD 配置已热重载: " + DescribeSettings());
        }

        public override void OnUpdate()
        {
            if (!_settings.Enabled) return;

            try
            {
                bool down = InputService.IsKeyHeld(_settings.Key);
                bool hide = _hotkey.Update(down, UnityTime.RealtimeSinceStartup, _settings.Mode, _settings.HoldSeconds);

                if (hide)
                {
                    _hider.Apply(_settings, true);
                }
                else if (_applied)
                {
                    _hider.Clear();
                }

                if (hide != _applied)
                {
                    _applied = hide;
                    Log.Info(hide ? "HUD 已隐藏 (" + DescribeScope() + ", " + _hider.HiddenCount
                        + " 个元素)" : "HUD 已恢复");
                    if (_settings.Notify)
                    {
                        UiService.Notify(hide ? "HUD 已隐藏" : "HUD 已恢复",
                            UiNotificationLevel.Info, 1.2f, Context);
                    }
                }
            }
            catch (Exception e)
            {
                // 场景切换的瞬间可能拿不到 UI，节流记录，不要刷屏。
                float now = UnityTime.RealtimeSinceStartup;
                string message = e.GetType().Name + ": " + e.Message;
                if (message != _lastError && now >= _nextErrorLog)
                {
                    _lastError = message;
                    _nextErrorLog = now + 15f;
                    Log.Warn("隐藏 HUD 失败: " + message);
                }
            }
        }

        public override void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            _hider.Reset();
            if (!_hotkey.Hidden) _applied = false;
        }

        public override void OnUnload()
        {
            _hider.Clear();
            _hotkey.Reset();
            _applied = false;
            Log.Info("隐藏HUD 已卸载，HUD 已恢复");
        }

        private string DescribeSettings()
        {
            return "key=" + _settings.Key
                + ", mode=" + (_settings.Mode == HudPressMode.Toggle ? "Toggle" : "Hold")
                + (_settings.Mode == HudPressMode.Hold ? ", holdSeconds=" + _settings.HoldSeconds.ToString("0.00") : "")
                + ", " + DescribeScope();
        }

        private string DescribeScope()
        {
            if (_settings.HideAllHud) return "全部 HUD";
            return "手牌=" + OnOff(_settings.HideHandCards)
                + ", 怪物信息=" + OnOff(_settings.HideMonsterDisplay)
                + ", 头像=" + OnOff(_settings.HideAvatars)
                + ", 其他=" + OnOff(_settings.HideOther);
        }

        private static string OnOff(bool value) { return value ? "开" : "关"; }
    }
}

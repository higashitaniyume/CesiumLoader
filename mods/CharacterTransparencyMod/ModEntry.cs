using System;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using FairyGUI;
using UnityEngine.SceneManagement;

namespace CharacterTransparencyMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            SdkManifest.ExportSidecar();
            ModBase.Run(new CharacterTransparencyController(), tag: "CharacterTransparency");
        }
    }

    /// <summary>
    /// 角色半透明控制器。
    /// 响应快捷键，使角色精灵半透明化，方便看清地块内容和重叠目标。
    /// </summary>
    public sealed class CharacterTransparencyController : ModBase
    {
        private CharacterTransparencySettings _settings = new CharacterTransparencySettings();
        private readonly TransparencyHotkeyState _hotkey = new TransparencyHotkeyState();
        private readonly CharacterTransparencyApplier _applier = new CharacterTransparencyApplier();

        private bool _wasActive;
        private float _nextErrorLog;

        public override string Name { get { return "角色半透明"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get
            {
                return "按 H 使游戏中的角色半透明化（默认 30% 不透明度），方便观察地块与重叠单位；支持长按与点击切换。";
            }
        }

        public override void OnInitialize()
        {
            CharacterTransparencySettings.EnsureDefaults(Config);
            _settings = CharacterTransparencySettings.Read(Config, warning => Log.Warn(warning));

            // 初始化时先执行一次恢复，清除非正常退出或前次会话遗留的半透明状态
            _applier.Restore();
            _hotkey.Reset();
            _wasActive = false;

            Log.Info("角色半透明已加载: " + DescribeSettings());
            if (!_settings.Enabled)
            {
                Log.Info("角色半透明当前处于禁用状态（enabled=false）。");
                return;
            }

            if (_settings.HoldMode)
                Log.Info("长按 " + _settings.Key + " 使角色半透明（不透明度 " + (_settings.Opacity * 100f).ToString("0") + "%），松开恢复。");
            else
                Log.Info("按一下 " + _settings.Key + " 切换角色半透明/正常。");
        }

        public override void OnConfigChanged()
        {
            var old = _settings;
            _settings = CharacterTransparencySettings.Read(Config, warning => Log.Warn(warning));

            bool modeOrKeyChanged = old.Key != _settings.Key || old.HoldMode != _settings.HoldMode;
            if (!_settings.Enabled || modeOrKeyChanged)
            {
                _applier.Restore();
                _hotkey.Reset(InputService.IsKeyHeld(_settings.Key), UnityTime.RealtimeSinceStartup);
                _wasActive = false;
            }
            else if (_hotkey.Active)
            {
                _applier.Apply(_settings.Opacity);
            }

            if (!_settings.Enabled)
                Log.Info("角色半透明已禁用（enabled=false）");
            else
                Log.Info("角色半透明配置已重载: " + DescribeSettings());
        }

        public override void OnUpdate()
        {
            if (!_settings.Enabled) return;

            try
            {
                // 输入框正在打字或键盘被接管时不响应快捷键
                bool inputBlocked = InputService.IsKeyboardCaptured ||
                    (GRoot.inst != null && GRoot.inst.focus is GTextInput);

                bool down = !inputBlocked && InputService.IsKeyHeld(_settings.Key);
                bool active = _hotkey.Update(down, UnityTime.RealtimeSinceStartup, _settings.HoldMode, _settings.HoldSeconds);

                if (active)
                {
                    _applier.Apply(_settings.Opacity);
                    _wasActive = true;
                }
                else if (_wasActive)
                {
                    _applier.Restore();
                    _wasActive = false;
                }
            }
            catch (Exception ex)
            {
                if (UnityTime.RealtimeSinceStartup >= _nextErrorLog)
                {
                    _nextErrorLog = UnityTime.RealtimeSinceStartup + 5f;
                    Log.Warn("角色半透明处理异常: " + ex.Message);
                }
            }
        }

        public override void OnLateUpdate()
        {
            if (!_settings.Enabled) return;

            try
            {
                if (_hotkey.Active)
                {
                    _applier.MaintainTrackedAlphas(_settings.Opacity);
                }
            }
            catch { }
        }

        public override void OnSceneUnloaded(Scene scene)
        {
            _applier.Restore();
            _hotkey.Reset();
            _wasActive = false;
        }

        public override void OnUnload()
        {
            _applier.Restore();
            _hotkey.Reset();
            _wasActive = false;
            Log.Info("角色半透明已卸载");
        }

        private string DescribeSettings()
        {
            return "enabled=" + _settings.Enabled
                + ", toggleKey=" + _settings.Key
                + ", holdMode=" + _settings.HoldMode
                + ", opacity=" + _settings.Opacity.ToString("0.00")
                + ", holdSeconds=" + _settings.HoldSeconds.ToString("0.00");
        }
    }
}

using System;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Mods;
using UnityEngine.SceneManagement;

namespace PinnedRelicsMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new PinnedRelicsController(), tag: "PinnedRelics"); }
    }

    public sealed class PinnedRelicsController : ModBase
    {
        private volatile Settings _settings;
        private RelicOverlay _overlay;
        private float _nextRefresh, _nextWarning;
        private bool _reset, _reportedDetail;

        public override string Name => "关键筹码置顶";
        public override string Version => "1.2.2";
        public override string Author => "Aperfish";
        public override string Description => "在左侧对应玩家的星币下方横排显示指定筹码图标。";

        public override void OnInitialize()
        {
            Settings.Defaults(Config);
            LoadSettings();
            _overlay = new RelicOverlay();
            _overlay.OnShown = message => Log.Info(message);
            _overlay.OnProblem = message => Log.Warn(message);
            _overlay.OnDiag = message => Log.Info(message);
            Log.Info("关键筹码置顶 1.2.2：使用游戏原生筹码图案和品质 UI，无外置图片；保留原有大小和位置配置。");
        }

        public override void OnConfigChanged() { LoadSettings(); }

        private void LoadSettings()
        {
            Settings settings;
            try { settings = Settings.Load(Config); }
            catch (Exception exception)
            {
                Log.Error("置顶名单初始化失败：" + exception.ToString());
                throw;
            }
            _settings = settings;
            if (settings.UnknownNames.Length > 0)
                Log.Warn("未识别的置顶筹码名称，已跳过：" + string.Join("、", settings.UnknownNames) + "。请查看随包《支持的筹码.md》。");
            Log.Info("置顶名单已读取，共 " + settings.Selection.Length + " 种筹码。");
        }

        public override void OnUpdate()
        {
            if (_overlay == null || _settings == null) return;
            float now = UnityTime.RealtimeSinceStartup;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 0.2f;
            try
            {
                if (_reset) { _reset = false; _overlay.OnSceneChanged(); }
                _overlay.Tick(now, _settings);
            }
            catch (Exception exception)
            {
                _overlay.HideAll();
                if (now >= _nextWarning)
                {
                    _nextWarning = now + 15;
                    var error = exception.GetBaseException();
                    Log.Warn("筹码图标暂时无法刷新：" + (_reportedDetail ? error.Message : error.ToString()));
                    _reportedDetail = true;
                }
            }
        }

        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) { _reset = true; }

        public override void OnUnload()
        {
            _overlay?.Dispose();
            _overlay = null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using GameLogic;
using party.model;

[assembly: ModManifest("手牌卡面皮肤", CardSkinMod.ModEntry.ModVersion, "CesiumLoader",
    "自定义手牌、事件、命运、地图事件及图鉴卡面，支持素材名、ID和中文名，静态和视频异画保留原版，未指定卡面保留原版",
    Permissions = ModPermission.ReadGameState | ModPermission.UI, SdkVersion = "2.3.2")]

namespace CardSkinMod
{
    public sealed class ModEntry : ModBase
    {
        public const string ModVersion = "1.1.1";

        public override string Name => "手牌卡面皮肤";
        public override string Version => ModVersion;

        private CardSkinModel _model;
        private CardTextureLoader _loader;
        private CardSkinApplier _applier;

        private bool _enabled = true;
        private string _activeSkin = "default";
        private bool _forceFullCard = true;

        private long _nextPoll;
        private long _nextCachePoll;

        public static void Main()
        {
            SdkManifest.ExportSidecar();
            Run(new ModEntry(), tag: "CardSkin");
        }

        public override void OnInitialize()
        {
            _enabled = Config?.GetBool("Enabled", true) ?? true;
            _activeSkin = Config?.GetString("ActiveSkin", "default") ?? "default";
            _forceFullCard = Config?.GetBool("ForceFullCard", false) ?? false;

            Config?.Set("Enabled", _enabled);
            Config?.Set("ActiveSkin", _activeSkin);
            Config?.Set("ForceFullCard", _forceFullCard);
            Config?.Save();

            _model = new CardSkinModel();
            _loader = new CardTextureLoader();
            _applier = new CardSkinApplier(_model, _loader, _forceFullCard);

            if (!_enabled)
            {
                SdkLog.Info("CardSkin", "模组已禁用。");
                return;
            }

            string skinDirectory = ResolveSkinDirectory(_activeSkin);
            bool loaded = _model.LoadSkinDirectory(skinDirectory, _activeSkin);

            if (loaded)
            {
                SdkLog.Info("CardSkin", $"已加载皮肤方案 [{_model.SkinName}] (路径: {skinDirectory})，包含 {_model.FileCount} 张自定义卡图。");
            }
            else
            {
                SdkLog.Warn("CardSkin", $"皮肤方案 [{_activeSkin}] 目录不存在或为空 ({skinDirectory})，将自动全量回退官方卡面。");
            }

            // 订阅手牌与对局事件
            GameEvents.HandChanged += OnHandChanged;
            GameEvents.StartAutoHook();

            SdkLog.Info("CardSkin", "卡面皮肤模组初始化完成：手牌正面自定义与回退机制已就绪。");
        }

        private void OnHandChanged(long playerId, IReadOnlyList<CardInfo> cards)
        {
            if (!_enabled || _applier == null) return;

            try
            {
                // 手牌变动时立即触发一次 UI 刷新
                _applier.ApplyToHandCardPanel();
                _applier.ApplyToFightWindow();
            }
            catch { }
        }

        public override void OnUpdate()
        {
            if (!_enabled || _applier == null) return;

            long now = DateTime.UtcNow.Ticks / 10000;
            if (now < _nextPoll) return;
            _nextPoll = now + 200; // 每 200ms 检测一次

            try
            {
                // 大厅也维护缓存；场景切换清空 TextureManager 后会在下一轮恢复。
                if (now >= _nextCachePoll)
                {
                    _nextCachePoll = now + 1000;
                    _applier.InjectTextureManager();
                }
                _applier.ApplyToVisibleCards();
            }
            catch (Exception ex)
            {
                SdkLog.Warn("CardSkin", "Update 轮询异常: " + ex.Message);
            }
        }

        public override void OnUnload()
        {
            try
            {
                GameEvents.HandChanged -= OnHandChanged;
                _applier?.RemoveInjectedTextures();
                _loader?.Dispose();
                SdkLog.Info("CardSkin", "模组已卸载并释放全部缓存贴图。");
            }
            catch { }
        }

        /// <summary>
        /// 解析皮肤目录绝对路径：
        /// 1. AstralParty_ModLoader/skins/<skin_name>
        /// 2. 兄弟目录 ../skins/<skin_name>
        /// </summary>
        private static string ResolveSkinDirectory(string skinName)
        {
            if (string.IsNullOrEmpty(skinName)) skinName = "default";

            // 优先从 CESIUM_MODS_DIR 推导: <游戏目录>/AstralParty_ModLoader/skins/<skinName>
            string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (!string.IsNullOrEmpty(modsDir))
            {
                string loaderRoot = Path.GetDirectoryName(modsDir);
                if (!string.IsNullOrEmpty(loaderRoot))
                {
                    string candidate = Path.Combine(loaderRoot, "skins", skinName);
                    return candidate;
                }
            }

            // 其次尝试从当前 Mod 目录推导
            string modDir = ModContext.Current?.Directory;
            if (!string.IsNullOrEmpty(modDir))
            {
                try
                {
                    string modsRoot = Path.GetDirectoryName(modDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string loaderRoot = string.IsNullOrEmpty(modsRoot) ? null : Path.GetDirectoryName(modsRoot);
                    if (!string.IsNullOrEmpty(loaderRoot))
                    {
                        return Path.Combine(loaderRoot, "skins", skinName);
                    }
                }
                catch { }
            }

            // HybridCLR 不支持 AppDomain.BaseDirectory，不能在方法体内引用该 API。
            // 加载器目录信息缺失时明确失败，避免依赖游戏进程的工作目录。
            throw new InvalidOperationException("无法定位皮肤目录：CESIUM_MODS_DIR 和模组目录均不可用。");
        }
    }
}

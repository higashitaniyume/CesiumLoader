using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Audio;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;

[assembly: ModManifest("音效方案替换", AudioSkinMod.ModEntry.ModVersion, "CesiumLoader",
    "音效方案替换模组，支持自定义 UI、对局、技能、战斗、语音及背景音乐，支持多方案切换",
    Permissions = ModPermission.ReadGameState | ModPermission.UI, SdkVersion = "3.0.0", Platforms = ModPlatform.Windows | ModPlatform.Android)]

namespace AudioSkinMod
{
    public sealed class ModEntry : ModBase
    {
        public const string ModVersion = "1.1.0";

        public override string Name => "音效方案替换";
        public override string Version => ModVersion;

        private AudioSkinSettings _settings;
        private string _loadedScheme = string.Empty;
        private long _nextCheckTicks;

        public static void Main()
        {
            SdkManifest.ExportSidecar();
            Run(new ModEntry(), tag: "AudioSkin");
        }

        public override void OnInitialize()
        {
            _settings = AudioSkinSettings.Load(Config);
            AudioSkinConfigDocs.Apply(Config);
            _settings.Save(Config);

            if (!_settings.Enabled)
            {
                SdkLog.Info("AudioSkin", "AudioSkinMod is disabled in config.");
                return;
            }

            AudioService.SetChannelVolume(AudioChannel.Sfx, _settings.SfxVolume);
            AudioService.SetChannelVolume(AudioChannel.Bgm, _settings.BgmVolume);
            AudioService.SetChannelVolume(AudioChannel.Voice, _settings.VoiceVolume);
            AudioService.SyncVolumeFromGameSettings();

            ApplyActiveScheme(_settings.ActiveScheme);

            if (_settings.LogEvents)
            {
                AudioEvents.AfterSoundPlay += OnAfterSoundPlay;
                AudioEvents.BeforeBgmPlay += OnBeforeBgmPlay;
            }

            SdkLog.Info("AudioSkin", "AudioSkinMod initialized successfully.");
        }

        public override void OnUpdate()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < _nextCheckTicks) return;
            _nextCheckTicks = now + TimeSpan.FromSeconds(2).Ticks;

            // Check if settings changed
            if (Config != null)
            {
                string active = Config.GetString("activeScheme", _settings.ActiveScheme);
                bool enabled = Config.GetBool("enabled", _settings.Enabled);
                if (active != _loadedScheme || enabled != _settings.Enabled)
                {
                    _settings.ActiveScheme = active;
                    _settings.Enabled = enabled;
                    if (_settings.Enabled)
                    {
                        ApplyActiveScheme(active);
                    }
                    else
                    {
                        AudioService.ClearReplacements();
                        AudioService.ClearBgmOverride();
                        _loadedScheme = string.Empty;
                        SdkLog.Info("AudioSkin", "AudioSkinMod disabled dynamically.");
                    }
                }
            }
        }

        public override void OnUnload()
        {
            AudioService.ClearReplacements();
            AudioService.ClearBgmOverride();
            AudioEvents.AfterSoundPlay -= OnAfterSoundPlay;
            AudioEvents.BeforeBgmPlay -= OnBeforeBgmPlay;
            SdkLog.Info("AudioSkin", "AudioSkinMod unloaded.");
        }

        private void OnAfterSoundPlay(int configId, string eventName)
        {
            if (_settings?.LogEvents == true)
            {
                SdkLog.Info("AudioSkin", $"[AudioEvent] ConfigId={configId}, Name={eventName}");
            }
        }

        private void OnBeforeBgmPlay(object sender, BgmPlayEventArgs e)
        {
            if (_settings?.LogEvents != true) return;

            string action = !e.Cancel ? "original" : (e.CustomClip != null ? "replace" : "mute");
            SdkLog.Info("AudioSkin",
                $"[AudioEvent] BGM Id={e.EventConfigId}, Name={e.EventName}, Bank={e.BankId}, State={e.StateName}, Action={action}");
        }

        public void ApplyActiveScheme(string schemeId)
        {
            AudioService.ClearReplacements();
            AudioService.ClearBgmOverride();
            _loadedScheme = string.Empty;

            if (string.IsNullOrEmpty(schemeId)) schemeId = "default";

            string schemeDir = ResolveSchemeDirectory(schemeId);
            if (!Directory.Exists(schemeDir))
            {
                SdkLog.Warn("AudioSkin", $"Audio scheme directory not found: {schemeDir}, using default sound effects.");
                _loadedScheme = schemeId;
                return;
            }

            string manifestPath = Path.Combine(schemeDir, "audio-scheme.json");
            if (!File.Exists(manifestPath))
            {
                manifestPath = Path.Combine(schemeDir, "scheme.json");
            }

            if (!File.Exists(manifestPath))
            {
                SdkLog.Info("AudioSkin", $"No audio-scheme.json found in {schemeDir}. Using default sounds.");
                _loadedScheme = schemeId;
                return;
            }

            try
            {
                string json = File.ReadAllText(manifestPath);
                var manifest = CesiumJson.Deserialize<AudioSchemeManifest>(json);
                if (manifest?.Rules != null)
                {
                    int replacedCount = 0;
                    int mutedCount = 0;
                    int redirectCount = 0;
                    int bgmReplacedCount = 0;
                    int bgmMutedCount = 0;

                    foreach (var rule in manifest.Rules)
                    {
                        if (rule == null) continue;
                        string action = (rule.Action ?? "replace").ToLowerInvariant();
                        bool isBgm = IsBgmRule(rule);
                        WarnIfUnsupportedBgmEvent(rule, isBgm);

                        if (action == "mute")
                        {
                            if (rule.ConfigId > 0)
                                Register(AudioReplacementRule.Mute(rule.ConfigId), isBgm);
                            else if (!string.IsNullOrEmpty(rule.EventName))
                                Register(AudioReplacementRule.MuteByName(rule.EventName), isBgm);
                            mutedCount++;
                            if (isBgm) bgmMutedCount++;
                        }
                        else if (action == "redirect" && rule.RedirectId > 0 && rule.ConfigId > 0)
                        {
                            AudioService.RegisterReplacement(AudioReplacementRule.Redirect(rule.ConfigId, rule.RedirectId));
                            redirectCount++;
                        }
                        else if (action == "replace" && !string.IsNullOrEmpty(rule.File))
                        {
                            string soundFile = Path.Combine(schemeDir, rule.File);
                            if (File.Exists(soundFile))
                            {
                                var clip = AudioService.LoadWav(soundFile);
                                if (clip != null)
                                {
                                    float vol = rule.Volume > 0 ? rule.Volume : 1.0f;
                                    if (rule.ConfigId > 0)
                                        Register(AudioReplacementRule.ForConfigId(rule.ConfigId, clip, vol), isBgm);
                                    if (!string.IsNullOrEmpty(rule.EventName))
                                        Register(AudioReplacementRule.ForEventName(rule.EventName, clip, vol), isBgm);
                                    replacedCount++;
                                    if (isBgm) bgmReplacedCount++;
                                }
                                else
                                {
                                    SdkLog.Warn("AudioSkin", $"未能解码或创建 AudioClip: {soundFile}");
                                }
                            }
                            else
                            {
                                SdkLog.Warn("AudioSkin", $"Sound file not found: {soundFile} for event {rule.EventName ?? rule.ConfigId.ToString()}");
                            }
                        }
                    }

                    _loadedScheme = schemeId;
                    SdkLog.Info("AudioSkin", $"Applied audio scheme [{manifest.Name ?? schemeId}]: {replacedCount} replaced ({bgmReplacedCount} BGM), {mutedCount} muted ({bgmMutedCount} BGM), {redirectCount} redirected.");
                }
            }
            catch (Exception ex)
            {
                SdkLog.Error("AudioSkin", $"Failed to parse audio scheme from {manifestPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// 注册一条替换规则，并按事件类型补齐通道与循环设置。
        ///
        /// 背景音乐走的是游戏自己的 BGM 播放链（BGMHelper → AudioManager → BGMModule → Wwise），
        /// SDK 会按「游戏正在播放哪首 BGM」重新判定规则：命中自定义音频时用循环 BGM 覆盖并静音原版，
        /// 命中静音规则时只静音原版；换曲/停止时自动收尾，不需要 Mod 自己管理。
        /// 通道与 loop 在这里只是把玩家意图声明清楚（loop 也决定覆盖时是否循环）。
        /// </summary>
        private void Register(AudioReplacementRule rule, bool isBgm)
        {
            if (rule == null) return;

            if (isBgm)
            {
                rule.Channel = AudioChannel.Bgm;
                rule.Loop = _settings?.BgmLoop ?? true;
            }

            AudioService.RegisterReplacement(rule);
        }

        /// <summary>判断一条方案规则是否作用于背景音乐。</summary>
        private static bool IsBgmRule(AudioSchemeRule rule)
        {
            if (rule == null) return false;
            if (rule.ConfigId > 0 && AudioEvents.IsBgmEvent(rule.ConfigId)) return true;

            // 配表尚未就绪时的兜底：音效目录里 BGM 分类的事件名都以 BGM_ 开头。
            return !string.IsNullOrEmpty(rule.EventName) &&
                   rule.EventName.StartsWith("BGM_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// BGM 停止事件（如 BGM_Login_Stop）与恢复事件（如 BGM_Resume）本身不播放音乐，
        /// 为它们配置替换不会有任何效果，这里提前给出提示。
        /// </summary>
        private static void WarnIfUnsupportedBgmEvent(AudioSchemeRule rule, bool isBgm)
        {
            if (!isBgm || rule.ConfigId <= 0) return;
            if (AudioEvents.IsBgmEvent(rule.ConfigId)) return; // 播放事件，正常生效

            string name = rule.EventName ?? rule.ConfigId.ToString();
            if (AudioEvents.IsBgmStopEvent(rule.ConfigId))
            {
                SdkLog.Warn("AudioSkin", $"事件 {name} 是 BGM 停止事件，替换不会生效；请改为替换对应的播放事件。");
            }
            else
            {
                SdkLog.Warn("AudioSkin", $"事件 {name} 不是 BGM 播放事件（可能是恢复/切换用的辅助事件），替换不会生效。");
            }
        }

        public static string ResolveSchemesRoot()
        {
            // 优先从加载器环境变量推导: <游戏目录>/AstralParty_ModLoader/audio-schemes
            string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (!string.IsNullOrEmpty(modsDir))
            {
                string loaderDir = Path.GetDirectoryName(modsDir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(loaderDir))
                {
                    return Path.Combine(loaderDir, "audio-schemes");
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
                        return Path.Combine(loaderRoot, "audio-schemes");
                    }
                }
                catch { }
            }

            // 本地测试离线运行兜底 (HybridCLR 中避开 AppDomain.BaseDirectory)
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
            {
                return Path.Combine(localApp, "AstralParty_ModLoader", "audio-schemes");
            }

            throw new InvalidOperationException("无法定位音效方案目录：CESIUM_MODS_DIR 环境变量不可用。");
        }

        public static string ResolveSchemeDirectory(string schemeId)
        {
            return Path.Combine(ResolveSchemesRoot(), schemeId);
        }
    }
}

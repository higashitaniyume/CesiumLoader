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
    Permissions = ModPermission.ReadGameState | ModPermission.UI, SdkVersion = "3.0.0")]

namespace AudioSkinMod
{
    public sealed class ModEntry : ModBase
    {
        public const string ModVersion = "1.0.0";

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
            SdkLog.Info("AudioSkin", "AudioSkinMod unloaded.");
        }

        private void OnAfterSoundPlay(int configId, string eventName)
        {
            if (_settings?.LogEvents == true)
            {
                SdkLog.Info("AudioSkin", $"[AudioEvent] ConfigId={configId}, Name={eventName}");
            }
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

                    foreach (var rule in manifest.Rules)
                    {
                        if (rule == null) continue;
                        string action = (rule.Action ?? "replace").ToLowerInvariant();

                        if (action == "mute")
                        {
                            if (rule.ConfigId > 0)
                                AudioService.RegisterReplacement(AudioReplacementRule.Mute(rule.ConfigId));
                            else if (!string.IsNullOrEmpty(rule.EventName))
                                AudioService.RegisterReplacement(AudioReplacementRule.MuteByName(rule.EventName));
                            mutedCount++;
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
                                        AudioService.RegisterReplacement(AudioReplacementRule.ForConfigId(rule.ConfigId, clip, vol));
                                    if (!string.IsNullOrEmpty(rule.EventName))
                                        AudioService.RegisterReplacement(AudioReplacementRule.ForEventName(rule.EventName, clip, vol));
                                    replacedCount++;
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
                    SdkLog.Info("AudioSkin", $"Applied audio scheme [{manifest.Name ?? schemeId}]: {replacedCount} replaced, {mutedCount} muted, {redirectCount} redirected.");
                }
            }
            catch (Exception ex)
            {
                SdkLog.Error("AudioSkin", $"Failed to parse audio scheme from {manifestPath}: {ex.Message}");
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

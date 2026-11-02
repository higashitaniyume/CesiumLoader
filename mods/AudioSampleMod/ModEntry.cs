using System;
using UnityEngine;
using CesiumLoader.SDK.Audio;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.UserInterface;

namespace AudioSampleMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new AudioSampleController(), tag: "AudioSample");
        }
    }

    /// <summary>
    /// 音频播放与替换示例模组。
    /// 
    /// 演示内容：
    /// 1. 使用 WavUtility 生成并解码内存合成音效；
    /// 2. 使用 AudioService.PlayOneShot 响应热键播放自定义音效；
    /// 3. 使用 AudioService.RegisterReplacement 拦截并替换游戏内置 UI 点击音效；
    /// 4. 监听 AudioEvents.BeforeSoundPlay 实时探查并输出游戏触发的每个音频事件（ID 与名称）。
    /// </summary>
    public sealed class AudioSampleController : ModBase
    {
        private AudioSampleSettings _settings = new AudioSampleSettings();
        private object _chimeClip;
        private object _clickClip;
        private EventHandler<SoundPlayEventArgs> _soundLogHandler;

        public override string Name => "音频示例";
        public override string Version => "1.0.0";
        public override string Author => "CesiumLoader";
        public override string Description => "演示使用 CesiumLoader SDK 播放自定义音效与拦截替换原版声音。按 F8 播放提示音，可在控制台观察游戏触发的声音事件。";

        public override void OnInitialize()
        {
            AudioSampleSettings.EnsureDefaults(Config);
            AudioSampleConfigDocs.Apply(Config);
            _settings = AudioSampleSettings.Read(Config, warn => Log.Warn(warn));

            Log.Info("音频示例模组已加载: enabled=" + _settings.Enabled + ", playKey=" + _settings.PlayKey);
            if (!_settings.Enabled) return;

            // 1. 生成内存示例音频片段（纯托管合成，无需预先放置文件）
            CreateSampleClips();

            // 2. 注册替换规则：替换包含 "click" 或 "btn" 的 UI 点击音效
            ApplyReplacementRules();

            // 3. 注册音频诊断监听器
            ApplyLogListener();

            Log.Info("音频示例已准备就绪！按 " + _settings.PlayKey + " 即可播放测试提示音。");
        }

        public override void OnConfigChanged()
        {
            _settings = AudioSampleSettings.Read(Config, warn => Log.Warn(warn));
            Log.Info("配置已更新: playKey=" + _settings.PlayKey + ", replaceButtonClick=" + _settings.ReplaceButtonClick + ", logSoundEvents=" + _settings.LogSoundEvents);

            // 重新应用替换与日志监听
            AudioService.ClearReplacements();
            if (_settings.Enabled)
            {
                ApplyReplacementRules();
                ApplyLogListener();
            }
            else
            {
                DetachLogListener();
            }
        }

        private void CreateSampleClips()
        {
            try
            {
                // 生成一个柔和的双音提示音 (523Hz C5)
                byte[] chimeWav = WavUtility.CreateSyntheticTone(523.25f, 0.25f, 0.6f);
                _chimeClip = AudioService.LoadWav(chimeWav, "SampleChime");

                // 生成一个清脆的点击音 (1200Hz 50ms 短促滴声)
                byte[] clickWav = WavUtility.CreateSyntheticTone(1200f, 0.05f, 0.5f);
                _clickClip = AudioService.LoadWav(clickWav, "SampleClick");
            }
            catch (Exception ex)
            {
                Log.Error("创建示例音频失败: " + ex.Message);
            }
        }

        private void ApplyReplacementRules()
        {
            if (_settings.ReplaceButtonClick && _clickClip != null)
            {
                // 使用基于谓词的动态替换：凡是名字包含 "click" 或 "btn" 的声音，替换为自定义短促滴声
                AudioService.RegisterReplacement(
                    filter: args => !string.IsNullOrEmpty(args.EventName) &&
                                   (args.EventName.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    args.EventName.IndexOf("btn", StringComparison.OrdinalIgnoreCase) >= 0),
                    resolver: _ => _clickClip,
                    volume: 0.7f,
                    channel: AudioChannel.Sfx
                );

                Log.Info("已启用 UI 点击声音替换（替换为自定义音效）。");
            }
        }

        private void ApplyLogListener()
        {
            DetachLogListener();
            if (_settings.LogSoundEvents)
            {
                _soundLogHandler = (sender, e) =>
                {
                    Log.Info(string.Format("[音频监听] EventConfigId={0}, EventName='{1}', WwiseId={2}, Cancel={3}",
                        e.EventConfigId, e.EventName, e.WwiseEventId, e.Cancel));
                };
                AudioEvents.BeforeSoundPlay += _soundLogHandler;
            }
        }

        private void DetachLogListener()
        {
            if (_soundLogHandler != null)
            {
                AudioEvents.BeforeSoundPlay -= _soundLogHandler;
                _soundLogHandler = null;
            }
        }

        public override void OnUpdate()
        {
            if (!_settings.Enabled) return;

            // 检测热键按下
            if (InputService.IsKeyDown(_settings.PlayKey))
            {
                if (_chimeClip != null)
                {
                    AudioService.PlayOneShot(_chimeClip, _settings.Volume, AudioChannel.Sfx);
                    UiService.Notify("播放自定义音效 (" + _settings.PlayKey + ")", UiNotificationLevel.Info);
                    Log.Info("触发热键 " + _settings.PlayKey + "，成功播放测试音频。");
                }
                else
                {
                    Log.Warn("测试音频片段尚未就绪。");
                }
            }
        }

        public override void OnUnload()
        {
            DetachLogListener();
            AudioService.ClearReplacements();
            base.OnUnload();
        }
    }
}

using System;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 音频替换规则匹配类型。
    /// </summary>
    public enum AudioMatchMode
    {
        /// <summary>按配置 ID 匹配。</summary>
        ConfigId,

        /// <summary>按事件名称精确匹配（忽略大小写）。</summary>
        EventNameExact,

        /// <summary>按事件名称前缀匹配（如 "Voice_Hero_1001_"）。</summary>
        EventNamePrefix,

        /// <summary>自定义谓词匹配。</summary>
        CustomPredicate
    }

    /// <summary>
    /// 注册在 AudioService 中的声音替换规则。
    /// </summary>
    public sealed class AudioReplacementRule
    {
        public AudioMatchMode MatchMode { get; }
        public int TargetConfigId { get; }
        public string TargetEventName { get; }
        public Predicate<SoundPlayEventArgs> Predicate { get; }

        public object Clip { get; }
        public string AudioFilePath { get; }
        public Func<SoundPlayEventArgs, object> Resolver { get; }

        public float Volume { get; set; } = 1.0f;
        public AudioChannel Channel { get; set; } = AudioChannel.Sfx;
        public int RedirectConfigId { get; set; }
        public bool IsMute { get; set; }

        public AudioReplacementRule(int configId, object clip, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            MatchMode = AudioMatchMode.ConfigId;
            TargetConfigId = configId;
            Clip = clip;
            Volume = volume;
            Channel = channel;
        }

        public AudioReplacementRule(int configId, string audioFilePath, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            MatchMode = AudioMatchMode.ConfigId;
            TargetConfigId = configId;
            AudioFilePath = audioFilePath;
            Volume = volume;
            Channel = channel;
        }

        public AudioReplacementRule(string eventName, object clip, bool prefix = false, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            MatchMode = prefix ? AudioMatchMode.EventNamePrefix : AudioMatchMode.EventNameExact;
            TargetEventName = eventName ?? string.Empty;
            Clip = clip;
            Volume = volume;
            Channel = channel;
        }

        public AudioReplacementRule(string eventName, string audioFilePath, bool prefix = false, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            MatchMode = prefix ? AudioMatchMode.EventNamePrefix : AudioMatchMode.EventNameExact;
            TargetEventName = eventName ?? string.Empty;
            AudioFilePath = audioFilePath;
            Volume = volume;
            Channel = channel;
        }

        public AudioReplacementRule(Predicate<SoundPlayEventArgs> predicate, Func<SoundPlayEventArgs, object> resolver, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            MatchMode = AudioMatchMode.CustomPredicate;
            Predicate = predicate;
            Resolver = resolver;
            Volume = volume;
            Channel = channel;
        }

        public static AudioReplacementRule ForConfigId(int configId, object clip, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            return new AudioReplacementRule(configId, clip, volume, channel);
        }

        public static AudioReplacementRule ForEventName(string eventName, object clip, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            return new AudioReplacementRule(eventName, clip, false, volume, channel);
        }

        public static AudioReplacementRule Mute(int configId)
        {
            return new AudioReplacementRule(configId, (object)null) { IsMute = true };
        }

        public static AudioReplacementRule MuteByName(string eventName)
        {
            return new AudioReplacementRule(eventName, (object)null) { IsMute = true };
        }

        public static AudioReplacementRule Redirect(int configId, int targetConfigId)
        {
            return new AudioReplacementRule(configId, (object)null) { RedirectConfigId = targetConfigId };
        }

        /// <summary>
        /// 检查此规则是否匹配当前声音事件。
        /// </summary>
        public bool Matches(SoundPlayEventArgs args)
        {
            if (args == null) return false;

            switch (MatchMode)
            {
                case AudioMatchMode.ConfigId:
                    return args.EventConfigId == TargetConfigId;

                case AudioMatchMode.EventNameExact:
                    return !string.IsNullOrEmpty(args.EventName) &&
                           string.Equals(args.EventName, TargetEventName, StringComparison.OrdinalIgnoreCase);

                case AudioMatchMode.EventNamePrefix:
                    return !string.IsNullOrEmpty(args.EventName) &&
                           args.EventName.StartsWith(TargetEventName, StringComparison.OrdinalIgnoreCase);

                case AudioMatchMode.CustomPredicate:
                    return Predicate != null && Predicate(args);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 解析用于替换的音频对象句柄。
        /// </summary>
        public object ResolveClip(SoundPlayEventArgs args)
        {
            if (Resolver != null) return Resolver(args);
            if (Clip != null) return Clip;
            if (!string.IsNullOrEmpty(AudioFilePath))
            {
                return AudioService.GetOrLoadClip(AudioFilePath);
            }
            return null;
        }
    }
}

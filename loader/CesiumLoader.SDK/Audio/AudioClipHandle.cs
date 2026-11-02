using System;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 音频片段包装句柄。封装底层 AudioClip 原生对象及其元数据。
    /// </summary>
    public sealed class AudioClipHandle
    {
        /// <summary>底层 AudioClip 原生对象（测试环境中可为模拟对象）。</summary>
        public object RawClip { get; }

        /// <summary>音频片段名称。</summary>
        public string Name { get; }

        /// <summary>时长（秒）。</summary>
        public float Duration { get; }

        /// <summary>声道数（1 为单声道，2 为立体声）。</summary>
        public int Channels { get; }

        /// <summary>采样率（Hz，如 44100、48000）。</summary>
        public int Frequency { get; }

        /// <summary>总采样点数。</summary>
        public int SampleCount { get; }

        /// <summary>来源文件路径（若从内存加载则可能为空）。</summary>
        public string SourcePath { get; }

        public AudioClipHandle(object rawClip, string name, float duration, int channels, int frequency, int sampleCount, string sourcePath = null)
        {
            RawClip = rawClip;
            Name = name ?? string.Empty;
            Duration = duration;
            Channels = channels;
            Frequency = frequency;
            SampleCount = sampleCount;
            SourcePath = sourcePath ?? string.Empty;
        }

        public override string ToString()
        {
            return string.Format("{0} ({1:0.00}s, {2}ch, {3}Hz)", Name, Duration, Channels, Frequency);
        }
    }
}

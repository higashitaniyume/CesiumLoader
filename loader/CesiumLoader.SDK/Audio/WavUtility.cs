using System;
using System.IO;
using System.Text;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 纯托管 WAV (RIFF/WAVE PCM) 编解码工具类。
    /// 
    /// 不依赖 Unity 引擎原生库，可在任意 .NET 运行时与单元测试中直接使用。
    /// 支持 8-bit、16-bit、24-bit 整数 PCM 与 32-bit 浮点 PCM 的解码，
    /// 以及 16-bit PCM 的生成与编码。
    /// </summary>
    public static class WavUtility
    {
        /// <summary>
        /// 从文件读取并解码 WAV 文件。
        /// </summary>
        public static bool TryDecode(string filePath, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null;
            channels = 0;
            sampleRate = 0;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                return TryDecode(bytes, out samples, out channels, out sampleRate);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从字节数组解码 WAV 音频。
        /// </summary>
        public static bool TryDecode(byte[] wavBytes, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null;
            channels = 0;
            sampleRate = 0;

            if (wavBytes == null || wavBytes.Length < 44)
                return false;

            try
            {
                // 1. RIFF 标志 (0..3)
                if (wavBytes[0] != 'R' || wavBytes[1] != 'I' || wavBytes[2] != 'F' || wavBytes[3] != 'F')
                    return false;

                // 2. WAVE 格式标志 (8..11)
                if (wavBytes[8] != 'W' || wavBytes[9] != 'A' || wavBytes[10] != 'V' || wavBytes[11] != 'E')
                    return false;

                int offset = 12;
                int format = 0;
                int bitsPerSample = 0;
                byte[] dataBytes = null;
                int dataLength = 0;

                while (offset + 8 <= wavBytes.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(wavBytes, offset, 4);
                    int chunkSize = BitConverter.ToInt32(wavBytes, offset + 4);
                    offset += 8;

                    if (chunkSize < 0 || offset + chunkSize > wavBytes.Length)
                        chunkSize = wavBytes.Length - offset;

                    if (chunkId == "fmt ")
                    {
                        if (chunkSize >= 16)
                        {
                            format = BitConverter.ToInt16(wavBytes, offset);
                            channels = BitConverter.ToInt16(wavBytes, offset + 2);
                            sampleRate = BitConverter.ToInt32(wavBytes, offset + 4);
                            bitsPerSample = BitConverter.ToInt16(wavBytes, offset + 14);
                        }
                    }
                    else if (chunkId == "data")
                    {
                        dataBytes = wavBytes;
                        dataLength = chunkSize;
                        break; // 找到 data 块
                    }

                    offset += chunkSize;
                    // RIFF 块对齐: 奇数长度填充 1 字节
                    if ((chunkSize & 1) == 1)
                        offset++;
                }

                if (dataBytes == null || channels <= 0 || sampleRate <= 0)
                    return false;

                // 支持 PCM (1) 或 IEEE Float (3)
                if (format != 1 && format != 3)
                    return false;

                int bytesPerSample = bitsPerSample / 8;
                if (bytesPerSample <= 0)
                    return false;

                int totalSamples = dataLength / bytesPerSample;
                samples = new float[totalSamples];

                int dataOffset = offset;
                if (format == 1) // PCM
                {
                    if (bitsPerSample == 16)
                    {
                        for (int i = 0; i < totalSamples; i++)
                        {
                            short val = BitConverter.ToInt16(dataBytes, dataOffset + i * 2);
                            samples[i] = val / 32768.0f;
                        }
                    }
                    else if (bitsPerSample == 8)
                    {
                        for (int i = 0; i < totalSamples; i++)
                        {
                            byte b = dataBytes[dataOffset + i];
                            samples[i] = (b - 128) / 128.0f;
                        }
                    }
                    else if (bitsPerSample == 24)
                    {
                        for (int i = 0; i < totalSamples; i++)
                        {
                            int byteIdx = dataOffset + i * 3;
                            int val = (dataBytes[byteIdx] | (dataBytes[byteIdx + 1] << 8) | (dataBytes[byteIdx + 2] << 16));
                            if ((val & 0x800000) != 0)
                                val |= unchecked((int)0xFF000000); // 符号扩展
                            samples[i] = val / 8388608.0f;
                        }
                    }
                    else if (bitsPerSample == 32)
                    {
                        for (int i = 0; i < totalSamples; i++)
                        {
                            int val = BitConverter.ToInt32(dataBytes, dataOffset + i * 4);
                            samples[i] = val / 2147483648.0f;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (format == 3) // IEEE Float
                {
                    if (bitsPerSample == 32)
                    {
                        for (int i = 0; i < totalSamples; i++)
                        {
                            samples[i] = BitConverter.ToSingle(dataBytes, dataOffset + i * 4);
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把浮点采样点编码为标准的 16 位立体声/单声道 WAV 字节数组。
        /// </summary>
        public static byte[] Encode(float[] samples, int channels, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || channels <= 0 || sampleRate <= 0)
                return new byte[0];

            int bytesPerSample = 2; // 16-bit
            int dataLength = samples.Length * bytesPerSample;
            int totalFileSize = 44 + dataLength;

            byte[] buffer = new byte[totalFileSize];
            using (var stream = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(stream))
            {
                // RIFF 标头
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(totalFileSize - 8);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt 子块
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // 块大小
                writer.Write((short)1); // PCM 格式
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bytesPerSample); // ByteRate
                writer.Write((short)(channels * bytesPerSample)); // BlockAlign
                writer.Write((short)16); // BitsPerSample

                // data 子块
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataLength);

                for (int i = 0; i < samples.Length; i++)
                {
                    float sample = Math.Max(-1.0f, Math.Min(1.0f, samples[i]));
                    short pcm = (short)(sample * 32767f);
                    writer.Write(pcm);
                }
            }

            return buffer;
        }

        /// <summary>
        /// 生成合成正弦波测试音频（附带淡入淡出包络，避免破音）。
        /// 常用于在缺少外部音频文件时即时生成占位提示音或自动化测试。
        /// </summary>
        public static byte[] CreateSyntheticTone(float frequencyHz = 440f, float durationSeconds = 0.3f, float volume = 0.5f, int sampleRate = 44100, int channels = 1)
        {
            if (durationSeconds <= 0f) durationSeconds = 0.1f;
            if (sampleRate <= 0) sampleRate = 44100;
            if (channels <= 0) channels = 1;

            int sampleCount = (int)(sampleRate * durationSeconds) * channels;
            float[] samples = new float[sampleCount];

            float attackSamples = sampleRate * 0.02f; // 20ms 淡入
            float releaseSamples = sampleRate * 0.05f; // 50ms 淡出
            int frames = sampleCount / channels;

            for (int i = 0; i < frames; i++)
            {
                float t = (float)i / sampleRate;
                float env = 1.0f;
                if (i < attackSamples) env = i / attackSamples;
                else if (i > frames - releaseSamples) env = (frames - i) / releaseSamples;

                float sample = (float)Math.Sin(2.0 * Math.PI * frequencyHz * t) * volume * env;
                for (int ch = 0; ch < channels; ch++)
                {
                    samples[i * channels + ch] = sample;
                }
            }

            return Encode(samples, channels, sampleRate);
        }
    }
}

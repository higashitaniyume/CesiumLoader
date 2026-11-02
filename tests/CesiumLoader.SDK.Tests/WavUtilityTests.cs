using System;
using System.IO;
using CesiumLoader.SDK.Audio;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class WavUtilityTests
    {
        [Fact]
        public void EncodeAndDecode_RoundTrip_PreservesAudioData()
        {
            int channels = 2;
            int sampleRate = 44100;
            float[] original = new float[sampleRate * channels]; // 1 秒立体声
            for (int i = 0; i < sampleRate; i++)
            {
                float t = (float)i / sampleRate;
                float s = (float)Math.Sin(2.0 * Math.PI * 440.0 * t) * 0.8f;
                original[i * 2] = s;
                original[i * 2 + 1] = -s;
            }

            byte[] wavBytes = WavUtility.Encode(original, channels, sampleRate);
            Assert.NotNull(wavBytes);
            Assert.True(wavBytes.Length > 44);

            bool ok = WavUtility.TryDecode(wavBytes, out float[] decoded, out int decChannels, out int decRate);
            Assert.True(ok);
            Assert.Equal(channels, decChannels);
            Assert.Equal(sampleRate, decRate);
            Assert.Equal(original.Length, decoded.Length);

            // 16 位量化误差在 1/32768 (约 0.0001) 内
            for (int i = 0; i < original.Length; i++)
            {
                Assert.InRange(Math.Abs(original[i] - decoded[i]), 0f, 0.001f);
            }
        }

        [Fact]
        public void CreateSyntheticTone_GeneratesValidWav()
        {
            byte[] tone = WavUtility.CreateSyntheticTone(880f, 0.2f, 0.6f, 48000, 1);
            Assert.NotNull(tone);

            bool ok = WavUtility.TryDecode(tone, out float[] samples, out int channels, out int sampleRate);
            Assert.True(ok);
            Assert.Equal(1, channels);
            Assert.Equal(48000, sampleRate);
            Assert.Equal(9600, samples.Length); // 0.2s * 48000
        }

        [Fact]
        public void Decode_CorruptData_ReturnsFalseGracefully()
        {
            Assert.False(WavUtility.TryDecode((byte[])null, out _, out _, out _));
            Assert.False(WavUtility.TryDecode(new byte[10], out _, out _, out _));
            Assert.False(WavUtility.TryDecode(new byte[50], out _, out _, out _)); // 全 0 不是合法 RIFF
        }

        [Fact]
        public void Decode_8BitPcm_DecodesCorrectly()
        {
            byte[] raw8BitWav = new byte[44 + 4];
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, raw8BitWav, 0, 4);
            BitConverter.GetBytes(40).CopyTo(raw8BitWav, 4);
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("WAVE"), 0, raw8BitWav, 8, 4);
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, raw8BitWav, 12, 4);
            BitConverter.GetBytes(16).CopyTo(raw8BitWav, 16);
            BitConverter.GetBytes((short)1).CopyTo(raw8BitWav, 20); // PCM
            BitConverter.GetBytes((short)1).CopyTo(raw8BitWav, 22); // Mono
            BitConverter.GetBytes(22050).CopyTo(raw8BitWav, 24); // Rate
            BitConverter.GetBytes(22050).CopyTo(raw8BitWav, 28); // ByteRate
            BitConverter.GetBytes((short)1).CopyTo(raw8BitWav, 32); // BlockAlign
            BitConverter.GetBytes((short)8).CopyTo(raw8BitWav, 34); // 8-bit
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("data"), 0, raw8BitWav, 36, 4);
            BitConverter.GetBytes(4).CopyTo(raw8BitWav, 40);

            // 8-bit PCM: 128 为中位 0，255 约为 +1，0 约为 -1
            raw8BitWav[44] = 128;
            raw8BitWav[45] = 255;
            raw8BitWav[46] = 0;
            raw8BitWav[47] = 64;

            bool ok = WavUtility.TryDecode(raw8BitWav, out float[] samples, out int ch, out int rate);
            Assert.True(ok);
            Assert.Equal(1, ch);
            Assert.Equal(22050, rate);
            Assert.Equal(4, samples.Length);
            Assert.InRange(samples[0], -0.01f, 0.01f);
            Assert.InRange(samples[1], 0.98f, 1.0f);
            Assert.InRange(samples[2], -1.0f, -0.98f);
        }

        [Fact]
        public void Decode_FileApi_DecodesSuccessfully()
        {
            string tempDir = TestEnv.NewTempDir("wav-test");
            string filePath = Path.Combine(tempDir, "test.wav");
            byte[] tone = WavUtility.CreateSyntheticTone(500f, 0.1f);
            File.WriteAllBytes(filePath, tone);

            bool ok = WavUtility.TryDecode(filePath, out float[] samples, out int ch, out int rate);
            Assert.True(ok);
            Assert.True(samples.Length > 0);
            Assert.Equal(1, ch);
        }
    }
}

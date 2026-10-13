using System;
using System.Collections.Generic;
using System.IO;

namespace CardSkinMod
{
    /// <summary>Version 1 frame sequence. Paths are derived, never supplied by the manifest.</summary>
    public sealed class CardAnimation
    {
        public string ManifestPath { get; private set; }
        public int Fps { get; private set; }
        public int FrameCount { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string FramePath(int index) => Path.Combine(Path.ChangeExtension(ManifestPath, "frames"), index.ToString("D6") + ".png");
        public int FrameAt(long elapsedMilliseconds)
        {
            if (elapsedMilliseconds <= 0) return 0;
            // Reduce before multiplying, including very long sessions.
            return (int)(((elapsedMilliseconds % (FrameCount * 1000L)) * Fps / 1000) % FrameCount);
        }
        public static bool TryLoad(string path, out CardAnimation animation)
        {
            animation = null;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(Path.GetDirectoryName(path)) & FileAttributes.ReparsePoint) != 0) return false;
                string text = File.ReadAllText(path);
                if (text.Length > 4096) return false;
                var values = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string raw in text.TrimStart('\ufeff').Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    int equal = line.IndexOf('=');
                    if (equal <= 0 || equal != line.LastIndexOf('=')) return false;
                    string key = line.Substring(0, equal).Trim();
                    string value = line.Substring(equal + 1).Trim();
                    if (key != "version" && key != "fps" && key != "frameCount" && key != "width" && key != "height") return false;
                    if (values.ContainsKey(key) || value.Length == 0) return false;
                    foreach (char ch in value) if (ch < '0' || ch > '9') return false;
                    if (!int.TryParse(value, out int number)) return false;
                    values[key] = number;
                }
                if (values.Count != 5 || values["version"] != 1) return false;
                int fps = values["fps"], count = values["frameCount"], w = values["width"], h = values["height"];
                if (fps < 1 || fps > 30 || count < 1 || count > 300 || w < 1 || h < 1 || w > 560 || h > 560 || (long)count * w * h > 60000000) return false;
                var result = new CardAnimation { ManifestPath = path, Fps = fps, FrameCount = count, Width = w, Height = h };
                string dir = Path.ChangeExtension(path, "frames");
                if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
                for (int i = 0; i < count; i++)
                {
                    string frame = result.FramePath(i);
                    if (!File.Exists(frame) || (File.GetAttributes(frame) & FileAttributes.ReparsePoint) != 0) return false;
                    byte[] png = File.ReadAllBytes(frame);
                    if (!ValidPngHeader(png, w, h)) return false;
                }
                animation = result;
                return true;
            }
            catch { return false; }
        }
        public static bool ValidPngHeader(byte[] bytes, int width, int height)
        {
            if (bytes == null || bytes.Length < 33) return false;
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < 8; i++) if (bytes[i] != signature[i]) return false;
            if (bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 13 || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82) return false;
            return ReadInt(bytes, 16) == width && ReadInt(bytes, 20) == height;
        }
        private static long ReadInt(byte[] bytes, int offset) => ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}

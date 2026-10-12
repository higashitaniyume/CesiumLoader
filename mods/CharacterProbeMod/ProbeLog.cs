using System;
using System.IO;
using System.Text;

namespace CharacterProbeMod
{
    // Deliberately uses only the file APIs already exercised in the game's IL2CPP BCL.
    public sealed class ProbeLog
    {
        public const int MaxCharacters = 32 * 1024 * 1024;
        public const int MaxPendingCharacters = 256 * 1024;
        private readonly string _path;
        private readonly StringBuilder _pending = new StringBuilder();
        public int Characters { get; private set; }
        public int Records { get; private set; }
        public int Dropped { get; private set; }
        public ProbeLog(string path) { _path = path; }
        public void Add(JsonRow row)
        {
            string line = row.ToString() + "\n";
            if (Characters + _pending.Length + line.Length > MaxCharacters || line.Length > MaxPendingCharacters)
            { Dropped++; return; }
            // Burst traffic may fill the buffer before the periodic two-second flush.
            // Drain existing rows first instead of dropping new frame/metadata records.
            if (_pending.Length + line.Length > MaxPendingCharacters) Flush();
            _pending.Append(line);
            Records++;
        }
        public void Flush()
        {
            if (_pending.Length == 0) return;
            string text = _pending.ToString();
            File.AppendAllText(_path, text);
            Characters += text.Length;
            _pending.Length = 0;
        }
    }

    public sealed class JsonRow
    {
        private readonly StringBuilder _text = new StringBuilder("{");
        private bool _first = true;
        private void Key(string key)
        {
            if (!_first) _text.Append(',');
            _first = false;
            Quote(_text, key);
            _text.Append(':');
        }
        public JsonRow S(string key, string value) { Key(key); Quote(_text, value ?? ""); return this; }
        public JsonRow N(string key, long value) { Key(key); _text.Append(value.ToString()); return this; }
        public JsonRow F(string key, float value)
        {
            Key(key);
            if (float.IsNaN(value) || float.IsInfinity(value)) _text.Append("null");
            else _text.Append(value.ToString("R").Replace(',', '.'));
            return this;
        }
        public JsonRow B(string key, bool value) { Key(key); _text.Append(value ? "true" : "false"); return this; }
        public override string ToString() { return _text.ToString() + "}"; }
        private static void Quote(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}

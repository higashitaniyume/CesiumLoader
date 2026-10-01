using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 配置: 每个 mod 一个独立 JSON 配置文件, 读写即存。
    /// 路径: &lt;游戏目录&gt;/AstralParty_ModLoader/configs/{modName}.json
    /// (通过 CESIUM_MODS_DIR 定位; 未设置时回退到 %LocalAppData%/AstralParty_ModLoader/configs)。
    ///
    /// 配置对象约定(手写 JSON, 零外部依赖):
    ///  - 公开字段(public field)才会被读写; 属性(property)忽略
    ///  - 支持类型: string / int / long / float / double / bool / 及它们的数组
    ///  - 示例: public class MyConfig { public bool Enabled = true; public int DelayMs = 100; }
    /// </summary>
    public static class SdkConfig
    {
        /// <summary>配置目录(自动创建)。</summary>
        public static string ConfigDirectory
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                string root;
                if (!string.IsNullOrEmpty(dir))
                {
                    // CESIUM_MODS_DIR 指向 <游戏目录>/AstralParty_ModLoader/mods
                    root = Path.GetFullPath(Path.Combine(dir, "..", "configs"));
                }
                else
                {
                    root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AstralParty_ModLoader", "configs");
                }
                try { Directory.CreateDirectory(root); } catch { }
                return root;
            }
        }

        /// <summary>mod 配置文件的完整路径(modName 非法字符会被替换为下划线)。</summary>
        public static string ConfigPath(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName)) throw new ArgumentException("modName 不能为空", nameof(modName));
            var safe = string.Concat(modName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(ConfigDirectory, safe + ".json");
        }

        /// <summary>读取配置; 文件不存在或损坏时返回 default(T)。</summary>
        public static T Load<T>(string modName) where T : class, new()
        {
            try
            {
                var path = ConfigPath(modName);
                if (!File.Exists(path)) return new T();
                var json = File.ReadAllText(path);
                return MiniJson.ParseObject<T>(json);
            }
            catch { return new T(); }
        }

        /// <summary>保存配置(写 JSON, 创建目录)。失败时静默返回 false。</summary>
        public static bool Save<T>(string modName, T value)
        {
            try
            {
                if (value == null) return false;
                var path = ConfigPath(modName);
                var json = MiniJson.SerializeObject(value);
                File.WriteAllText(path, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>配置是否存在。</summary>
        public static bool Exists(string modName)
        {
            try { return File.Exists(ConfigPath(modName)); }
            catch { return false; }
        }
    }

    /// <summary>
    /// 极简 JSON 序列化器(零依赖, netstandard2.0 原生可用)。
    /// 只处理 SdkConfig 需要的场景: 公有字段组成的扁平对象。
    /// </summary>
    internal static class MiniJson
    {
        public static string SerializeObject(object obj)
        {
            var sb = new StringBuilder();
            WriteObject(sb, obj);
            return sb.ToString();
        }

        public static T ParseObject<T>(string json) where T : class, new()
        {
            var result = new T();
            if (string.IsNullOrWhiteSpace(json)) return result;
            var body = TrimObjectBody(json);
            if (body == null) return result;

            var fields = typeof(T).GetFields();
            int i = 0;
            int len = body.Length;
            while (i < len)
            {
                // 跳过空白和逗号
                while (i < len && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
                if (i >= len) break;

                // 读 key
                if (body[i] != '"') return result; // 损坏, 返回默认
                i++;
                var keyStart = i;
                while (i < len && body[i] != '"') i++;
                if (i >= len) return result;
                var key = body.Substring(keyStart, i - keyStart);
                i++; // 跳过闭合引号

                while (i < len && char.IsWhiteSpace(body[i])) i++;
                if (i >= len || body[i] != ':') return result;
                i++; // 跳过冒号

                while (i < len && char.IsWhiteSpace(body[i])) i++;
                if (i >= len) return result;

                var field = fields.FirstOrDefault(f => f.Name == key);
                if (field != null)
                {
                    object value;
                    i = TryReadValue(body, i, field.FieldType, out value) ? i : i;
                    if (value != null)
                    {
                        try { field.SetValue(result, value); } catch { }
                    }
                }
                else
                {
                    i = SkipValue(body, i);
                }
            }
            return result;
        }

        private static string TrimObjectBody(string json)
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return json.Substring(start + 1, end - start - 1);
        }

        private static int SkipValue(string s, int i)
        {
            // 跳过字符串 / 数字 / 布尔 / null / 数组 / 对象
            if (s[i] == '"')
            {
                i++;
                while (i < s.Length) { if (s[i] == '\\') i += 2; else if (s[i] == '"') { i++; break; } else i++; }
                return i;
            }
            if (s[i] == '[' || s[i] == '{')
            {
                char open = s[i], close = open == '[' ? ']' : '}';
                int depth = 0;
                while (i < s.Length)
                {
                    if (s[i] == open) depth++;
                    else if (s[i] == close) { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                return i;
            }
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',') i++;
            return i;
        }

        private static bool TryReadValue(string s, int i, Type targetType, out object value)
        {
            value = null;
            if (i >= s.Length) return false;

            var elemType = targetType.IsArray ? targetType.GetElementType() : null;
            if (elemType != null)
            {
                if (s[i] != '[') return false;
                i++;
                var list = new System.Collections.Generic.List<object>();
                while (true)
                {
                    while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
                    if (i >= s.Length) break;
                    if (s[i] == ']') break;
                    object item;
                    if (TryReadValue(s, i, elemType, out item) && item != null)
                        list.Add(item);
                    // TryReadValue 不推进 i, 需要手动跳
                    i = SkipValue(s, i);
                }
                var arr = System.Array.CreateInstance(elemType, list.Count);
                for (int k = 0; k < list.Count; k++) arr.SetValue(list[k], k);
                value = arr;
                return true;
            }

            if (s[i] == '"')
            {
                i++;
                var start = i;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        sb.Append(s[i + 1] == 'n' ? '\n' : s[i + 1]);
                        i += 2;
                    }
                    else if (s[i] == '"') break;
                    else { sb.Append(s[i]); i++; }
                }
                var str = sb.ToString();
                if (targetType == typeof(string)) value = str;
                else if (targetType == typeof(int)) value = int.TryParse(str, out var iv) ? iv : 0;
                else if (targetType == typeof(long)) value = long.TryParse(str, out var lv) ? lv : 0L;
                else if (targetType == typeof(float)) value = float.TryParse(str, out var fv) ? fv : 0f;
                else if (targetType == typeof(double)) value = double.TryParse(str, out var dv) ? dv : 0d;
                else if (targetType == typeof(bool)) value = bool.TryParse(str, out var bv) && bv;
                return value != null;
            }

            // 数字 / 布尔 / null
            var start2 = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',') i++;
            var token = s.Substring(start2, i - start2);
            if (targetType == typeof(string)) { value = token; return true; }
            if (targetType == typeof(int)) { value = int.TryParse(token, out var iv) ? iv : 0; return true; }
            if (targetType == typeof(long)) { value = long.TryParse(token, out var lv) ? lv : 0L; return true; }
            if (targetType == typeof(float)) { value = float.TryParse(token, out var fv) ? fv : 0f; return true; }
            if (targetType == typeof(double)) { value = double.TryParse(token, out var dv) ? dv : 0d; return true; }
            if (targetType == typeof(bool)) { value = token == "true"; return true; }
            return false;
        }

        private static void WriteObject(StringBuilder sb, object obj)
        {
            sb.Append('{');
            var fields = obj.GetType().GetFields();
            bool first = true;
            foreach (var field in fields)
            {
                if (!field.IsPublic) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(field.Name).Append("\":");
                WriteValue(sb, field.GetValue(obj), field.FieldType);
            }
            sb.Append('}');
        }

        private static void WriteValue(StringBuilder sb, object value, Type type)
        {
            if (type.IsArray)
            {
                sb.Append('[');
                var arr = (System.Array)value;
                var elemType = type.GetElementType();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, arr.GetValue(i), elemType);
                }
                sb.Append(']');
                return;
            }
            if (value == null) { sb.Append("null"); return; }
            if (type == typeof(string)) { WriteString(sb, (string)value); return; }
            if (type == typeof(bool)) { sb.Append((bool)value ? "true" : "false"); return; }
            if (type == typeof(float) || type == typeof(double))
            {
                sb.Append(((IConvertible)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(value.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}

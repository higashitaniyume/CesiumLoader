using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 标记某个公共字段 / 属性<b>不参与</b> <see cref="CesiumJson"/> 的序列化。
    ///
    /// 典型用途: 快照类(如 <see cref="CameraState"/>)用标量字段承载真正的数据,
    /// 同时提供 <c>Vector3</c> / <c>Quaternion</c> 这类 Unity 结构体的便捷属性,
    /// 这些属性只是在标量之上的视图, 不能重复写进 JSON。
    ///
    /// 示例:
    /// <code>
    /// [CesiumJsonIgnore]
    /// public Vector3 Position { get { ... } set { ... } }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class CesiumJsonIgnoreAttribute : Attribute
    {
    }

    /// <summary>
    /// SDK 自带的极简 JSON 读写(无第三方依赖)。
    ///
    /// 与既有 <c>SdkConfig.MiniJson</c> 的分工: MiniJson 只支持"公共字段 + 一维基元数组",
    /// 为保持既有 mod 行为完全不变, 它保持原样; 本类用于新的 <see cref="ModConfig"/>,
    /// 额外支持: 枚举、嵌套简单对象、List/数组、Dictionary、属性(settable)。
    ///
    /// 不支持(故意): 多态类型信息、循环引用、泛型字典键(键必须是 string)。
    /// </summary>
    public static class CesiumJson
    {
        // =====================================================================
        // 序列化
        // =====================================================================

        /// <summary>把任意受支持的对象序列化为 JSON 文本。</summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        /// <summary>把字典序列化为美化 JSON(便于人工编辑 config.json)。</summary>
        public static string SerializePretty(object value)
        {
            var sb = new StringBuilder(512);
            WriteValue(sb, value, 0, true);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int depth, bool pretty = false)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string s) { WriteString(sb, s); return; }
            if (value is char c) { WriteString(sb, c.ToString()); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }

            if (value is Enum)
            {
                WriteString(sb, value.ToString());
                return;
            }

            if (value is float f) { sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is double d) { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is decimal dec) { sb.Append(dec.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IDictionary dict)
            {
                WriteObject(sb, dict, depth, pretty);
                return;
            }

            if (value is IEnumerable list && !(value is string))
            {
                WriteArray(sb, list, depth, pretty);
                return;
            }

            WriteObject(sb, ToDictionary(value), depth, pretty);
        }

        private static void WriteObject(StringBuilder sb, IDictionary dict, int depth, bool pretty)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                if (key == null) continue;
                if (!first) sb.Append(',');
                first = false;
                if (pretty) NewLine(sb, depth + 1);
                WriteString(sb, key);
                sb.Append(':');
                if (pretty) sb.Append(' ');
                WriteValue(sb, entry.Value, depth + 1, pretty);
            }
            if (pretty && !first) NewLine(sb, depth);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, int depth, bool pretty)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                if (pretty) NewLine(sb, depth + 1);
                WriteValue(sb, item, depth + 1, pretty);
            }
            if (pretty && !first) NewLine(sb, depth);
            sb.Append(']');
        }

        private static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>把 POCO 的公共字段 / 可读属性拍平成字典。</summary>
        public static Dictionary<string, object> ToDictionary(object value)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (value == null) return result;

            Type type = value.GetType();

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsDefined(typeof(CesiumJsonIgnoreAttribute), true)) continue;
                try { result[field.Name] = Normalize(field.GetValue(value)); }
                catch { }
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length != 0) continue;
                if (prop.IsDefined(typeof(CesiumJsonIgnoreAttribute), true)) continue;
                try { result[prop.Name] = Normalize(prop.GetValue(value, null)); }
                catch { }
            }

            return result;
        }

        private static object Normalize(object v)
        {
            if (v == null) return null;
            if (v is Enum) return v.ToString();
            return v;
        }

        // =====================================================================
        // 反序列化
        // =====================================================================

        /// <summary>解析 JSON 文本为 Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null。</summary>
        public static object Deserialize(string json)
        {
            object value;
            if (!TryDeserialize(json, out value)) return null;
            return value;
        }

        /// <summary>解析 JSON 文本, 失败返回 false(不抛异常)。</summary>
        public static bool TryDeserialize(string json, out object value)
        {
            value = null;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                int pos = 0;
                value = ParseValue(json, ref pos);
                SkipWhitespace(json, ref pos);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        /// <summary>把 JSON 映射为强类型对象(公共字段 + 可写属性 + List/数组 + 枚举 + 嵌套对象)。</summary>
        public static T Deserialize<T>(string json) where T : new()
        {
            object node;
            if (!TryDeserialize(json, out node)) return default(T);
            try { return (T)ToObject(node, typeof(T)); }
            catch { return default(T); }
        }

        /// <summary>把解析后的节点树映射为指定类型。</summary>
        public static object ToObject(object node, Type type)
        {
            if (type == null) return node;
            if (node == null) return DefaultOf(type);

            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return ToObject(node, underlying);

            if (type == typeof(object)) return node;
            if (type == typeof(string)) return node as string ?? Convert.ToString(node, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return ToBool(node, false);
            if (type == typeof(int)) return (int)ToDouble(node, 0);
            if (type == typeof(long)) return (long)ToDouble(node, 0);
            if (type == typeof(float)) return (float)ToDouble(node, 0);
            if (type == typeof(double)) return ToDouble(node, 0);
            if (type == typeof(byte)) return (byte)ToDouble(node, 0);
            if (type == typeof(short)) return (short)ToDouble(node, 0);
            if (type == typeof(uint)) return (uint)ToDouble(node, 0);
            if (type.IsEnum)
            {
                if (node is string es && !string.IsNullOrEmpty(es))
                {
                    try { return Enum.Parse(type, es, true); } catch { return DefaultOf(type); }
                }
                try { return Enum.ToObject(type, (int)ToDouble(node, 0)); } catch { return DefaultOf(type); }
            }

            if (type.IsArray)
            {
                var list = node as IList;
                if (list == null) return null;
                Type element = type.GetElementType();
                var array = Array.CreateInstance(element, list.Count);
                for (int i = 0; i < list.Count; i++) array.SetValue(ToObject(list[i], element), i);
                return array;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = node as IList;
                if (list == null) return null;
                Type element = type.GetGenericArguments()[0];
                var result = (IList)Activator.CreateInstance(type);
                for (int i = 0; i < list.Count; i++) result.Add(ToObject(list[i], element));
                return result;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var dict = node as IDictionary<string, object>;
                if (dict == null) return null;
                Type[] args = type.GetGenericArguments();
                var result = (IDictionary)Activator.CreateInstance(type);
                foreach (var kv in dict) result[kv.Key] = ToObject(kv.Value, args[1]);
                return result;
            }

            var map = node as IDictionary<string, object>;
            if (map == null) return DefaultOf(type);

            object instance;
            try { instance = Activator.CreateInstance(type); }
            catch { return DefaultOf(type); }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object raw;
                if (!map.TryGetValue(field.Name, out raw)) continue;
                if (field.IsDefined(typeof(CesiumJsonIgnoreAttribute), true)) continue;
                try { field.SetValue(instance, ToObject(raw, field.FieldType)); } catch { }
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;
                if (prop.GetIndexParameters().Length != 0) continue;
                if (prop.IsDefined(typeof(CesiumJsonIgnoreAttribute), true)) continue;
                object raw;
                if (!map.TryGetValue(prop.Name, out raw)) continue;
                try { prop.SetValue(instance, ToObject(raw, prop.PropertyType), null); } catch { }
            }

            return instance;
        }

        // =====================================================================
        // 访问辅助
        // =====================================================================

        /// <summary>读字符串。</summary>
        public static string GetString(IDictionary<string, object> map, string key, string fallback = null)
        {
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            if (raw is string s) return s;
            return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? fallback;
        }

        /// <summary>读 int。</summary>
        public static int GetInt(IDictionary<string, object> map, string key, int fallback = 0)
        {
            return (int)GetDouble(map, key, fallback);
        }

        /// <summary>读 float。</summary>
        public static float GetFloat(IDictionary<string, object> map, string key, float fallback = 0f)
        {
            return (float)GetDouble(map, key, fallback);
        }

        /// <summary>读 double。</summary>
        public static double GetDouble(IDictionary<string, object> map, string key, double fallback = 0)
        {
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            return ToDouble(raw, fallback);
        }

        /// <summary>读 bool。</summary>
        public static bool GetBool(IDictionary<string, object> map, string key, bool fallback = false)
        {
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            return ToBool(raw, fallback);
        }

        /// <summary>读枚举(字符串不区分大小写; 数字按底层 int)。</summary>
        public static T GetEnum<T>(IDictionary<string, object> map, string key, T fallback = default(T)) where T : struct
        {
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return fallback;
            try
            {
                if (raw is string s) return (T)Enum.Parse(typeof(T), s, true);
                return (T)Enum.ToObject(typeof(T), (int)ToDouble(raw, 0));
            }
            catch { return fallback; }
        }

        /// <summary>读子对象为强类型。</summary>
        public static T GetObject<T>(IDictionary<string, object> map, string key) where T : class
        {
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return null;
            return ToObject(raw, typeof(T)) as T;
        }

        /// <summary>读数组为 List&lt;T&gt;。</summary>
        public static List<T> GetList<T>(IDictionary<string, object> map, string key)
        {
            var result = new List<T>();
            object raw;
            if (map == null || !map.TryGetValue(key, out raw)) return result;
            var list = raw as IList;
            if (list == null) return result;
            for (int i = 0; i < list.Count; i++)
            {
                try { result.Add((T)ToObject(list[i], typeof(T))); } catch { }
            }
            return result;
        }

        // =====================================================================
        // 解析内核
        // =====================================================================

        private static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("JSON 意外结束");

            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't':
                    Expect(s, ref pos, "true");
                    return true;
                case 'f':
                    Expect(s, ref pos, "false");
                    return false;
                case 'n':
                    Expect(s, ref pos, "null");
                    return null;
                default:
                    return ParseNumber(s, ref pos);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            pos++; // {
            SkipWhitespace(s, ref pos);

            if (pos < s.Length && s[pos] == '}') { pos++; return map; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[pos] != '"') throw new FormatException("JSON 键必须是字符串, 位置 " + pos);

                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("JSON 缺少 ':'");
                pos++;
                map[key] = ParseValue(s, ref pos);

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return map; }
                throw new FormatException("JSON 对象中出现意外字符 '" + s[pos] + "'");
            }
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // [
            SkipWhitespace(s, ref pos);

            if (pos < s.Length && s[pos] == ']') { pos++; return list; }

            while (true)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("JSON 数组未闭合");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return list; }
                throw new FormatException("JSON 数组中出现意外字符 '" + s[pos] + "'");
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();

            while (true)
            {
                if (pos >= s.Length) throw new FormatException("JSON 字符串未闭合");
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (pos >= s.Length) throw new FormatException("JSON 转义未结束");
                char esc = s[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new FormatException("JSON \\u 转义不完整");
                        sb.Append((char)Convert.ToInt32(s.Substring(pos, 4), 16));
                        pos += 4;
                        break;
                    default: throw new FormatException("未知转义 \\" + esc);
                }
            }
        }

        private static double ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length)
            {
                char c = s[pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') pos++;
                else break;
            }
            if (pos == start) throw new FormatException("JSON 数字格式错误, 位置 " + start);

            string raw = s.Substring(start, pos - start);
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException("JSON 数字无法解析: " + raw);
            return value;
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new FormatException("JSON 期望 '" + literal + "'");
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }

        private static double ToDouble(object node, double fallback)
        {
            if (node == null) return fallback;
            if (node is double d) return d;
            if (node is float f) return f;
            if (node is bool b) return b ? 1 : 0;
            if (node is string s)
            {
                double parsed;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
                return fallback;
            }
            try { return Convert.ToDouble(node, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool ToBool(object node, bool fallback)
        {
            if (node == null) return fallback;
            if (node is bool b) return b;
            if (node is double d) return Math.Abs(d) > double.Epsilon;
            if (node is string s)
            {
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                double parsed;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return Math.Abs(parsed) > double.Epsilon;
            }
            return fallback;
        }

        private static object DefaultOf(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}

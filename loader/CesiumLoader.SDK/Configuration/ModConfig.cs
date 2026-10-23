using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
namespace CesiumLoader.SDK.Configuration
{
    /// <summary>
    /// 每个 mod 独立的配置: <c>mods\{ModId}\config.json</c>。
    ///
    /// 用法:
    /// <code>
    /// var cfg = ModConfig.ForMod(ModContext.Current);   // 或 ModBase 子类的 Config 属性
    /// float speed = cfg.GetFloat("speed", 20f);
    /// cfg.Set("speed", 35f);
    /// cfg.Save();
    /// </code>
    ///
    /// 特点:
    ///  - 零第三方依赖(内部用 <see cref="CesiumJson"/>)。
    ///  - 文件缺失/损坏时不抛异常: 用默认值继续跑, 并把损坏文件备份为 config.json.bak。
    ///  - 支持 int/long/float/double/bool/string/enum/嵌套简单对象/List。
    /// </summary>
    public sealed class ModConfig
    {
        private static readonly Dictionary<string, ModConfig> _cache =
            new Dictionary<string, ModConfig>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        private readonly object _lock = new object();
        private Dictionary<string, object> _data = new Dictionary<string, object>(StringComparer.Ordinal);
        private bool _dirty;
        private string _lastLoadedText;
        private long _revision;

        /// <summary>配置版本号(每次从磁盘加载新内容或保存修改时递增)。</summary>
        public long Revision
        {
            get { lock (_lock) { return _revision; } }
        }

        /// <summary>配置内容发生变更(重载成功或保存)时触发。</summary>
        public event Action<ModConfig> Changed;

        private ModConfig(string modId, string path)
        {
            ModId = string.IsNullOrEmpty(modId) ? "Unknown" : modId;
            Path = path;
        }

        /// <summary>所属 mod 标识。</summary>
        public string ModId { get; private set; }

        /// <summary>配置文件绝对路径(无法定位目录时为 null)。</summary>
        public string Path { get; private set; }

        /// <summary>是否有未保存的修改。</summary>
        public bool IsDirty { get { lock (_lock) { return _dirty; } } }

        /// <summary>全部键值(只读快照)。</summary>
        public IReadOnlyDictionary<string, object> All
        {
            get { lock (_lock) { return new Dictionary<string, object>(_data, StringComparer.Ordinal); } }
        }

        // =====================================================================
        // 构造
        // =====================================================================

        /// <summary>取(或创建)指定 mod 的配置, 首次调用时自动从磁盘加载。</summary>
        public static ModConfig ForMod(ModContext context)
        {
            if (context == null) return ForModId("Unknown");
            return ForModId(context.ModId, context.Directory);
        }

        /// <summary>按 ModId 取配置。</summary>
        public static ModConfig ForModId(string modId, string modDirectory = null)
        {
            if (string.IsNullOrEmpty(modId)) modId = "Unknown";

            lock (_cacheLock)
            {
                ModConfig existing;
                if (_cache.TryGetValue(modId, out existing)) return existing;

                string path = ResolvePath(modId, modDirectory);
                var config = new ModConfig(modId, path);
                _cache[modId] = config;
                config.Load();
                return config;
            }
        }

        /// <summary>清空缓存(测试用)。</summary>
        public static void ClearCache()
        {
            lock (_cacheLock) { _cache.Clear(); }
        }

        private static string ResolvePath(string modId, string modDirectory)
        {
            try
            {
                if (!string.IsNullOrEmpty(modDirectory))
                    return System.IO.Path.Combine(modDirectory, "config.json");

                string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                if (!string.IsNullOrEmpty(modsDir))
                    return System.IO.Path.Combine(modsDir, modId, "config.json");

                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(root, "AstralParty_ModLoader", "configs", modId + ".json");
            }
            catch { return null; }
        }

        // =====================================================================
        // 读
        // =====================================================================

        /// <summary>是否包含某个键。</summary>
        public bool Has(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock) { return _data.ContainsKey(key); }
        }

        /// <summary>键数量。</summary>
        public int Count { get { lock (_lock) { return _data.Count; } } }

        /// <summary>读值(类型不匹配时返回默认值, 不抛异常)。</summary>
        public T Get<T>(string key, T defaultValue = default(T))
        {
            if (string.IsNullOrEmpty(key)) return defaultValue;
            object raw;
            lock (_lock)
            {
                if (!_data.TryGetValue(key, out raw)) return defaultValue;
            }

            T value;
            return TryConvert(raw, out value) ? value : defaultValue;
        }

        /// <summary>
        /// 严格转换: 只有确实能转成 T 才返回 true。
        /// 不能依赖 CesiumJson.ToObject —— 它对 "12x" → int 会给出 0, 于是调用方传入的
        /// 默认值(例如 GetInt("label", 42) 里的 42)会被 0 悄悄顶掉, 表现为"配置读出来是 0"。
        /// </summary>
        private static bool TryConvert<T>(object raw, out T value)
        {
            value = default(T);
            if (raw == null) return false;

            Type target = typeof(T);

            try
            {
                if (raw is T) { value = (T)raw; return true; }

                string text = raw as string;
                if (text != null)
                {
                    if (target == typeof(string)) { value = (T)(object)text; return true; }

                    if (target == typeof(int))
                    {
                        int v;
                        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
                        value = (T)(object)v; return true;
                    }
                    if (target == typeof(long))
                    {
                        long v;
                        if (!long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
                        value = (T)(object)v; return true;
                    }
                    if (target == typeof(float))
                    {
                        float v;
                        if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
                        value = (T)(object)v; return true;
                    }
                    if (target == typeof(double))
                    {
                        double v;
                        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
                        value = (T)(object)v; return true;
                    }
                    if (target == typeof(bool))
                    {
                        bool v;
                        if (!bool.TryParse(text, out v)) return false;
                        value = (T)(object)v; return true;
                    }
                    if (target.IsEnum)
                    {
                        try
                        {
                            if (Enum.IsDefined(target, text)) { value = (T)Enum.Parse(target, text, true); return true; }
                        }
                        catch { }
                        return false;
                    }
                }

                object converted = CesiumJson.ToObject(raw, target);
                if (converted != null && target.IsInstanceOfType(converted)) { value = (T)converted; return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>读值, 失败返回 false。</summary>
        public bool TryGet<T>(string key, out T value)
        {
            value = default(T);
            if (string.IsNullOrEmpty(key) || !Has(key)) return false;
            value = Get<T>(key, default(T));
            return true;
        }

        /// <summary>读 int。</summary>
        public int GetInt(string key, int defaultValue = 0) { return Get(key, defaultValue); }

        /// <summary>读 float。</summary>
        public float GetFloat(string key, float defaultValue = 0f) { return Get(key, defaultValue); }

        /// <summary>读 double。</summary>
        public double GetDouble(string key, double defaultValue = 0d) { return Get(key, defaultValue); }

        /// <summary>读 bool。</summary>
        public bool GetBool(string key, bool defaultValue = false) { return Get(key, defaultValue); }

        /// <summary>读 string。</summary>
        public string GetString(string key, string defaultValue = null) { return Get(key, defaultValue); }

        /// <summary>读枚举(大小写不敏感)。</summary>
        public TEnum GetEnum<TEnum>(string key, TEnum defaultValue = default(TEnum)) where TEnum : struct
        {
            return Get(key, defaultValue);
        }

        // =====================================================================
        // 写
        // =====================================================================

        /// <summary>设置值(标记为脏; 需要 Save 才落盘)。</summary>
        public void Set<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                _data[key] = CesiumJson.ToDictionary(new Wrapper<T> { Value = value })["Value"];
                _dirty = true;
            }
        }

        /// <summary>若键不存在则设置默认值(标记为脏), 返回是否新设了默认值。</summary>
        public bool SetDefault<T>(string key, T defaultValue)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                if (_data.ContainsKey(key)) return false;
                _data[key] = CesiumJson.ToDictionary(new Wrapper<T> { Value = defaultValue })["Value"];
                _dirty = true;
                return true;
            }
        }

        /// <summary>删除键。</summary>
        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                bool removed = _data.Remove(key);
                if (removed) _dirty = true;
                return removed;
            }
        }

        /// <summary>清空全部键。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _data.Clear();
                _dirty = true;
            }
        }

        // =====================================================================
        // 落盘
        // =====================================================================

        /// <summary>从磁盘重新加载(丢弃未保存修改)。</summary>
        public bool Reload()
        {
            lock (_lock)
            {
                _data = new Dictionary<string, object>(StringComparer.Ordinal);
                _dirty = false;
            }
            bool ok = Load();
            if (ok)
            {
                try { Changed?.Invoke(this); } catch { }
            }
            return ok;
        }

        /// <summary>从磁盘加载; 文件不存在时返回 false 且保持为空(不报错)。</summary>
        public bool Load()
        {
            try
            {
                if (string.IsNullOrEmpty(Path) || !File.Exists(Path)) return false;

                string text = File.ReadAllText(Path);
                object parsed;
                if (!CesiumJson.TryDeserialize(text, out parsed))
                {
                    SdkLog.Warn(ModId, "config.json 解析失败, 使用默认值; 原文件备份为 .bak");
                    TryBackup();
                    return false;
                }

                var map = parsed as Dictionary<string, object>;
                if (map == null)
                {
                    SdkLog.Warn(ModId, "config.json 顶层不是 JSON 对象, 忽略");
                    return false;
                }

                lock (_lock)
                {
                    _data = map;
                    _dirty = false;
                    _lastLoadedText = text;
                    _revision++;
                }
                SdkLog.Debug(ModId, "已加载配置: " + Path + " (" + map.Count + " 项)");
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Warn(ModId, "读配置失败: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 检查磁盘配置文件是否变化; 若内容变化且解析成功则重载, 更新 Revision 并触发 Changed 事件。
        /// 若解析失败则保留当前配置不变(绝不破坏磁盘文件)。
        /// </summary>
        public bool ReloadIfChanged()
        {
            try
            {
                if (string.IsNullOrEmpty(Path) || !File.Exists(Path)) return false;

                string text;
                try { text = File.ReadAllText(Path); }
                catch { return false; }

                lock (_lock)
                {
                    if (string.Equals(text, _lastLoadedText, StringComparison.Ordinal))
                        return false;
                }

                object parsed;
                if (!CesiumJson.TryDeserialize(text, out parsed))
                {
                    SdkLog.Warn(ModId, "外部修改的 config.json 解析失败, 保留当前配置不变");
                    return false;
                }

                var map = parsed as Dictionary<string, object>;
                if (map == null)
                {
                    SdkLog.Warn(ModId, "外部修改的 config.json 顶层不是 JSON 对象, 忽略");
                    return false;
                }

                lock (_lock)
                {
                    _data = map;
                    _dirty = false;
                    _lastLoadedText = text;
                    _revision++;
                }

                SdkLog.Info(ModId, "已热重载配置: " + Path + " (版本 " + _revision + ", " + map.Count + " 项)");
                try { Changed?.Invoke(this); }
                catch (Exception e) { SdkLog.Error(ModId, "配置变更事件异常: " + e.Message); }
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Warn(ModId, "热重载检查异常: " + e.Message);
                return false;
            }
        }

        /// <summary>轮询已缓存的全部配置, 热重载有变更的配置。</summary>
        public static void PollAllChanged()
        {
            ModConfig[] configs;
            lock (_cacheLock)
            {
                if (_cache.Count == 0) return;
                configs = new ModConfig[_cache.Count];
                _cache.Values.CopyTo(configs, 0);
            }

            for (int i = 0; i < configs.Length; i++)
            {
                try { configs[i].ReloadIfChanged(); }
                catch { }
            }
        }

        /// <summary>保存到磁盘; 返回是否成功。</summary>
        public bool Save()
        {
            try
            {
                if (string.IsNullOrEmpty(Path)) return false;

                string json;
                lock (_lock) { json = CesiumJson.SerializePretty(_data); }

                string dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

                // 先写临时文件再替换, 避免断电/崩溃写坏配置
                string tmp = Path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(tmp, Path);

                lock (_lock)
                {
                    _dirty = false;
                    _lastLoadedText = json;
                    _revision++;
                }
                SdkLog.Debug(ModId, "已保存配置: " + Path);
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Error(ModId, "保存配置失败: " + e.Message);
                return false;
            }
        }

        /// <summary>有修改时才保存。</summary>
        public bool SaveIfDirty()
        {
            if (!IsDirty) return true;
            return Save();
        }

        private void TryBackup()
        {
            try
            {
                if (string.IsNullOrEmpty(Path) || !File.Exists(Path)) return;
                string bak = Path + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(Path, bak);
            }
            catch { }
        }

        /// <summary>用于把任意值规整成 JSON 节点的小包装。</summary>
        private sealed class Wrapper<TValue>
        {
            public TValue Value;
        }

        /// <summary>诊断用文本。</summary>
        public override string ToString()
        {
            return "ModConfig[" + ModId + "] " + Count + " 项" + (IsDirty ? " (未保存)" : "") +
                   (string.IsNullOrEmpty(Path) ? "" : " @" + Path);
        }

        internal static string FormatInvariant(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}

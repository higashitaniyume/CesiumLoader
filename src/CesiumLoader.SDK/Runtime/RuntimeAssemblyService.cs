using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 运行时类型/成员解析服务(带缓存)。
    ///
    /// 为什么不用 <c>Assembly.GetType(name, true)</c>: 本游戏是 IL2CPP + HybridCLR,
    /// 热更程序集里按字符串取类型不可靠(项目既有的 managed Bootstrap 就因此被弃用)。
    /// 这里改为: 遍历已加载程序集 → 建立 FullName→Type 索引 → 查表。
    ///
    /// 缓存: 每个程序集只建一次索引; 类型/成员查找结果都进缓存, 场景切换或显式
    /// <see cref="InvalidateCaches"/> 时清空。
    ///
    /// 性能约定: 解析只在首次发生, 之后是字典查表; 但 <b>不要每帧对新类型名调用</b>。
    /// </summary>
    public static class RuntimeAssemblyService
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<Assembly, Dictionary<string, Type>> _typeIndex =
            new Dictionary<Assembly, Dictionary<string, Type>>();
        private static readonly Dictionary<string, Type> _typeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _simpleNameCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private static long _typeLookups;
        private static long _typeHits;
        private static long _typeMisses;

        // =====================================================================
        // 统计
        // =====================================================================

        /// <summary>类型查找次数。</summary>
        public static long TypeLookupCount { get { return Interlocked.Read(ref _typeLookups); } }

        /// <summary>类型缓存命中次数。</summary>
        public static long TypeCacheHits { get { return Interlocked.Read(ref _typeHits); } }

        /// <summary>类型查找失败次数(用于判断某个游戏类型是否真的存在)。</summary>
        public static long TypeMissCount { get { return Interlocked.Read(ref _typeMisses); } }

        // =====================================================================
        // 程序集
        // =====================================================================

        /// <summary>当前已加载的全部程序集(失败返回空数组)。</summary>
        public static Assembly[] GetAssemblies()
        {
            try { return AppDomain.CurrentDomain.GetAssemblies() ?? new Assembly[0]; }
            catch (Exception e)
            {
                SdkLog.Error("RUNTIME", "枚举程序集失败: " + e.Message);
                return new Assembly[0];
            }
        }

        /// <summary>已加载程序集名列表(诊断用)。</summary>
        public static List<string> GetAssemblyNames()
        {
            var names = new List<string>();
            foreach (var asm in GetAssemblies())
            {
                try
                {
                    var name = asm.GetName();
                    if (name != null && !string.IsNullOrEmpty(name.Name)) names.Add(name.Name);
                }
                catch { }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>按简单名查找程序集(不区分大小写)。</summary>
        public static Assembly FindAssembly(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            foreach (var asm in GetAssemblies())
            {
                try
                {
                    var name = asm.GetName();
                    if (name != null && string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return asm;
                }
                catch { }
            }
            return null;
        }

        /// <summary>程序集是否已加载。</summary>
        public static bool IsAssemblyLoaded(string simpleName)
        {
            return FindAssembly(simpleName) != null;
        }

        // =====================================================================
        // 类型
        // =====================================================================

        /// <summary>按完整名查找类型(不区分大小写); 找不到返回 null。</summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            lock (_lock)
            {
                Type cached;
                if (_typeCache.TryGetValue(fullName, out cached)) { Interlocked.Increment(ref _typeHits); return cached; }
            }

            Interlocked.Increment(ref _typeLookups);
            Type found = null;

            foreach (var asm in GetAssemblies())
            {
                var index = GetTypeIndex(asm);
                if (index == null) continue;
                if (index.TryGetValue(fullName, out found) && found != null) break;
                found = null;
            }

            if (found == null) Interlocked.Increment(ref _typeMisses);

            lock (_lock) { _typeCache[fullName] = found; }
            return found;
        }

        /// <summary>在指定程序集内查找类型。</summary>
        public static Type FindType(string assemblySimpleName, string fullName)
        {
            var asm = FindAssembly(assemblySimpleName);
            if (asm == null) return null;
            var index = GetTypeIndex(asm);
            Type found;
            return index != null && index.TryGetValue(fullName, out found) ? found : null;
        }

        /// <summary>按简单名(不含命名空间)查找类型; 有歧义时返回第一个并记录警告。</summary>
        public static Type FindTypeBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;

            lock (_lock)
            {
                Type cached;
                if (_simpleNameCache.TryGetValue(simpleName, out cached)) { Interlocked.Increment(ref _typeHits); return cached; }
            }

            Interlocked.Increment(ref _typeLookups);
            Type found = null;
            string foundFullName = null;

            foreach (var asm in GetAssemblies())
            {
                var index = GetTypeIndex(asm);
                if (index == null) continue;

                foreach (var pair in index)
                {
                    var type = pair.Value;
                    if (type == null || type.Name != simpleName) continue;

                    if (found == null)
                    {
                        found = type;
                        foundFullName = pair.Key;
                    }
                    else
                    {
                        SdkLog.Warn("RUNTIME", "简单名 '" + simpleName + "' 有歧义: " +
                                             foundFullName + " 与 " + pair.Key + "; 使用前者");
                        break;
                    }
                }

                if (found != null) break;
            }

            if (found == null) Interlocked.Increment(ref _typeMisses);

            lock (_lock) { _simpleNameCache[simpleName] = found; }
            return found;
        }

        /// <summary>类型是否存在。</summary>
        public static bool HasType(string fullName)
        {
            return FindType(fullName) != null;
        }

        /// <summary>查找所有基类名匹配的类型(诊断用; 较慢, 慎用)。</summary>
        public static List<Type> FindTypesByBaseName(string baseTypeName)
        {
            var result = new List<Type>();
            if (string.IsNullOrEmpty(baseTypeName)) return result;

            foreach (var asm in GetAssemblies())
            {
                var index = GetTypeIndex(asm);
                if (index == null) continue;

                foreach (var pair in index)
                {
                    var type = pair.Value;
                    if (type == null) continue;
                    try
                    {
                        var cursor = type.BaseType;
                        int guard = 0;
                        while (cursor != null && guard++ < 32)
                        {
                            if (string.Equals(cursor.Name, baseTypeName, StringComparison.Ordinal) ||
                                string.Equals(cursor.FullName, baseTypeName, StringComparison.Ordinal))
                            {
                                result.Add(type);
                                break;
                            }
                            cursor = cursor.BaseType;
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        private static Dictionary<string, Type> GetTypeIndex(Assembly assembly)
        {
            if (assembly == null) return null;

            lock (_lock)
            {
                Dictionary<string, Type> existing;
                if (_typeIndex.TryGetValue(assembly, out existing)) return existing;
            }

            var index = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (Exception e)
                {
                    // 不要单独 catch ReflectionTypeLoadException 再读 e.Types:
                    // HybridCLR 裁剪掉了 System.Reflection.ReflectionTypeLoadException::get_Types,
                    // 游戏内一读就抛
                    //     MissingMethodException: MethodNotFind System.Reflection.ReflectionTypeLoadException::get_Types
                    // 而且这类裁剪异常会穿透 catch, 直接废掉调用方(2026-09-20 游戏内实测:
                    // CinemachineService.IsAvailable -> CameraProbe/Diagnostics 全挂)。
                    // 因此这里只当作普通失败处理: 该程序集退化为空索引, 其它程序集照常检索。
                    //
                    // 注意: 这确实会丢掉"部分加载"的类型。实测该分支在游戏内极少命中(SDK/游戏
                    // 程序集都能整体加载), 而离线测试里 SDK 程序集之所以命中, 是因为测试进程
                    // 缺少 UniTask 依赖 —— 已通过测试项目引用 extracted_dlls\UniTask.dll 解决,
                    // 不需要在这里为反射不可用的环境再开一条路径。
                    SdkLog.Debug("RUNTIME", "读取程序集类型失败 " + SafeAssemblyName(assembly) + ": " + e.Message);
                    types = new Type[0];
                }

                for (int i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    if (type == null) continue;
                    try
                    {
                        if (!string.IsNullOrEmpty(type.FullName)) index[type.FullName] = type;
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                SdkLog.Debug("RUNTIME", "建立类型索引失败 " + SafeAssemblyName(assembly) + ": " + e.Message);
            }

            lock (_lock) { _typeIndex[assembly] = index; }
            return index;
        }

        // =====================================================================
        // 成员解析
        // =====================================================================

        /// <summary>查找方法(按名字 + 可选参数个数)。</summary>
        public static MethodInfo FindMethod(Type type, string name, int parameterCount = -1, bool includeNonPublic = false)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                if (includeNonPublic) flags |= BindingFlags.NonPublic;

                var methods = type.GetMethods(flags);
                MethodInfo fallback = null;
                for (int i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                    if (parameterCount < 0) return method;
                    if (method.GetParameters().Length == parameterCount) return method;
                    if (fallback == null) fallback = method;
                }
                return fallback;
            }
            catch { return null; }
        }

        /// <summary>查找字段(含私有)。</summary>
        public static FieldInfo FindField(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type cursor = type;
                int guard = 0;
                while (cursor != null && guard++ < 32)
                {
                    var field = cursor.GetField(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    if (field != null) return field;
                    cursor = cursor.BaseType;
                }
            }
            catch { }
            return null;
        }

        /// <summary>查找属性(含私有)。</summary>
        public static PropertyInfo FindProperty(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type cursor = type;
                int guard = 0;
                while (cursor != null && guard++ < 32)
                {
                    var property = cursor.GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    if (property != null) return property;
                    cursor = cursor.BaseType;
                }
            }
            catch { }
            return null;
        }

        // =====================================================================
        // 安全访问
        // =====================================================================

        /// <summary>调用静态方法; 任何异常都被吞掉并记录, 返回 null。</summary>
        public static object SafeInvokeStatic(Type type, string methodName, object[] args = null, string tag = null)
        {
            if (type == null) return null;
            try
            {
                var method = FindMethod(type, methodName, args != null ? args.Length : -1);
                if (method == null)
                {
                    SdkLog.Debug("RUNTIME", "未找到方法 " + type.Name + "." + methodName);
                    return null;
                }
                return method.Invoke(null, args);
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash(tag ?? "RUNTIME", type.Name + "." + methodName + " 调用失败", Unwrap(e));
                return null;
            }
        }

        /// <summary>调用实例方法; 任何异常都被吞掉并记录, 返回 null。</summary>
        public static object SafeInvoke(object target, string methodName, object[] args = null, string tag = null)
        {
            if (target == null) return null;
            try
            {
                var type = target.GetType();
                var method = FindMethod(type, methodName, args != null ? args.Length : -1);
                if (method == null)
                {
                    SdkLog.Debug("RUNTIME", "未找到方法 " + type.Name + "." + methodName);
                    return null;
                }
                return method.Invoke(target, args);
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash(tag ?? "RUNTIME", target.GetType().Name + "." + methodName + " 调用失败", Unwrap(e));
                return null;
            }
        }

        /// <summary>读字段。</summary>
        public static object SafeGetField(object target, string fieldName, string tag = null)
        {
            if (target == null) return null;
            try
            {
                var field = FindField(target.GetType(), fieldName);
                return field != null ? field.GetValue(target) : null;
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash(tag ?? "RUNTIME", target.GetType().Name + "." + fieldName + " 读字段失败", Unwrap(e));
                return null;
            }
        }

        /// <summary>读字段(强类型)。</summary>
        public static T SafeGetField<T>(object target, string fieldName, T fallback = default(T), string tag = null)
        {
            object raw = SafeGetField(target, fieldName, tag);
            if (raw == null) return fallback;
            try { return (T)raw; }
            catch { return fallback; }
        }

        /// <summary>写字段。</summary>
        public static bool SafeSetField(object target, string fieldName, object value, string tag = null)
        {
            if (target == null) return false;
            try
            {
                var field = FindField(target.GetType(), fieldName);
                if (field == null) return false;
                field.SetValue(target, value);
                return true;
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash(tag ?? "RUNTIME", target.GetType().Name + "." + fieldName + " 写字段失败", Unwrap(e));
                return false;
            }
        }

        /// <summary>读属性。</summary>
        public static object SafeGetProperty(object target, string propertyName, string tag = null)
        {
            if (target == null) return null;
            try
            {
                var property = FindProperty(target.GetType(), propertyName);
                if (property == null || !property.CanRead) return null;
                return property.GetValue(target, null);
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash(tag ?? "RUNTIME", target.GetType().Name + "." + propertyName + " 读属性失败", Unwrap(e));
                return null;
            }
        }

        /// <summary>读属性(强类型)。</summary>
        public static T SafeGetProperty<T>(object target, string propertyName, T fallback = default(T), string tag = null)
        {
            object raw = SafeGetProperty(target, propertyName, tag);
            if (raw == null) return fallback;
            try { return (T)raw; }
            catch { return fallback; }
        }

        /// <summary>写属性。</summary>
        public static bool SafeSetProperty(object target, string propertyName, object value, string tag = null)
        {
            if (target == null) return false;
            try
            {
                var property = FindProperty(target.GetType(), propertyName);
                if (property == null || !property.CanWrite) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash(tag ?? "RUNTIME", target.GetType().Name + "." + propertyName + " 写属性失败", Unwrap(e));
                return false;
            }
        }

        // =====================================================================
        // 缓存
        // =====================================================================

        /// <summary>清空类型/成员缓存(场景切换后可调用, 通常不需要)。</summary>
        public static void InvalidateCaches()
        {
            lock (_lock)
            {
                _typeCache.Clear();
                _simpleNameCache.Clear();
            }
        }

        private static Exception Unwrap(Exception e)
        {
            var tie = e as TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : e;
        }

        private static string SafeAssemblyName(Assembly assembly)
        {
            try
            {
                var name = assembly.GetName();
                return name != null ? name.Name : "?";
            }
            catch { return "?"; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 托管侧的 IL2CPP 互操作层 —— 原生层 <c>il2cpp_safe.h</c> 的镜像。
    ///
    /// <para>
    /// <b>职责边界(重要):</b> 原生 Loader 负责"引导"(等 GameAssembly.dll、等 HybridCLR、
    /// 调用托管入口), 那是 C++ 的活; 本类只负责在<b>托管侧</b>按需访问同一批
    /// <c>GameAssembly.dll</c> 导出, 用于诊断和"托管反射够不到"的少数场景。
    /// mod 的常规类型访问应该优先走 <see cref="RuntimeAssemblyService"/>(托管反射,
    /// 有 HybridCLR 支持且错误信息友好), <b>不要</b>为了绕过反射问题而直接调这里。
    /// </para>
    ///
    /// <para>
    /// <b>安全纪律(与 il2cpp_safe.h 一致):</b>
    ///  - 所有导出在初始化时一次性解析并缓存, 任一必需导出缺失 ⇒ <see cref="IsInitialized"/> = false,
    ///    后续所有调用直接返回 null/false, <b>绝不</b>拿空指针去 call;
    ///  - 每次调用前做 domain / 句柄 / 参数 空检查;
    ///  - <c>il2cpp_runtime_invoke</c> 的托管异常被转译成可读文本(<see cref="LastError"/>), 不向 mod 抛;
    ///  - 任何托管异常都在此层吞掉并记日志 —— IL2CPP 互操作失败不能拖垮游戏进程。
    /// </para>
    ///
    /// <para>
    /// <b>无法完全兜住的边界(必须知道):</b> 如果图形 API 或 IL2CPP 版本不匹配,
    /// 错误的内存读写会直接产生 AccessViolation, 这类异常在 .NET 上默认不可捕获。
    /// 因此本类只做"元数据查询 + 受控 invoke", 不做任意指针运算, 也不暴露裸指针给 mod。
    /// </para>
    /// </summary>
    public static class Il2CppInteropService
    {
        // =====================================================================
        // 原生入口 (kernel32)
        // =====================================================================

        private static class Native
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr GetModuleHandleW(string lpModuleName);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
            internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        }

        // =====================================================================
        // 导出签名 —— 名称与 il2cpp_safe.h 的 il2cpp_tbl 一一对应
        // (x64 上调用约定统一, 这里仍显式标 Cdecl, 与原生头文件保持一致)
        // =====================================================================

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_DomainGet();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_AssemblyGetImage(IntPtr assembly);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ImageGetAssembly(IntPtr image);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ClassFromName(IntPtr image, string ns, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ClassGetMethodFromName(IntPtr klass, string name, int argc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ClassGetMethods(IntPtr klass, IntPtr iter);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_MethodGetName(IntPtr method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_MethodGetParam(IntPtr method, uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Fn_MethodGetParamCount(IntPtr method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_RuntimeInvoke(IntPtr method, IntPtr obj, IntPtr[] args, out IntPtr exception);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ThreadAttach(IntPtr domain);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ThreadCurrent();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_DomainGetAssemblies(IntPtr domain, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ImageGetName(IntPtr image);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_GetCorlib();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_TypeGetName(IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ArrayClassGet(IntPtr elementClass, uint rank);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ArrayNew(IntPtr arrayClass, IntPtr length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ArrayObjectHeaderSize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_ExceptionGetMessage(IntPtr exception);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Fn_StringChars(IntPtr il2cppString);

        // =====================================================================
        // 已解析的函数表
        // =====================================================================

        private sealed class Table
        {
            internal IntPtr Module;
            internal Fn_DomainGet DomainGet;
            internal Fn_AssemblyGetImage AssemblyGetImage;
            internal Fn_ImageGetAssembly ImageGetAssembly;
            internal Fn_ClassFromName ClassFromName;
            internal Fn_ClassGetMethodFromName ClassGetMethodFromName;
            internal Fn_ClassGetMethods ClassGetMethods;
            internal Fn_MethodGetName MethodGetName;
            internal Fn_MethodGetParam MethodGetParam;
            internal Fn_MethodGetParamCount MethodGetParamCount;
            internal Fn_RuntimeInvoke RuntimeInvoke;
            internal Fn_ThreadAttach ThreadAttach;
            internal Fn_ThreadCurrent ThreadCurrent;      // 可选
            internal Fn_DomainGetAssemblies DomainGetAssemblies;
            internal Fn_ImageGetName ImageGetName;
            internal Fn_GetCorlib GetCorlib;
            internal Fn_TypeGetName TypeGetName;
            internal Fn_ArrayClassGet ArrayClassGet;
            internal Fn_ArrayNew ArrayNew;
            internal Fn_ArrayObjectHeaderSize ArrayObjectHeaderSize;
            internal Fn_ExceptionGetMessage ExceptionGetMessage;   // 可选
            internal Fn_StringChars StringChars;                   // 可选

            /// <summary>与 il2cpp_safe.h 的 complete() 同一套必需项判定。</summary>
            internal bool Complete
            {
                get
                {
                    return DomainGet != null && AssemblyGetImage != null && ImageGetAssembly != null &&
                           ClassFromName != null && ClassGetMethodFromName != null && ClassGetMethods != null &&
                           MethodGetName != null && MethodGetParam != null && MethodGetParamCount != null &&
                           RuntimeInvoke != null && ThreadAttach != null && DomainGetAssemblies != null &&
                           ImageGetName != null && GetCorlib != null && TypeGetName != null &&
                           ArrayClassGet != null && ArrayNew != null && ArrayObjectHeaderSize != null;
                }
            }
        }

        private static readonly object _lock = new object();
        private static Table _table;
        private static bool _resolveAttempted;
        private static string _lastError;
        private static string _failReason;

        // 程序集名 → Il2CppAssembly* 缓存(带 TTL, 可显式失效; 见 InvalidateCache)
        private static readonly Dictionary<string, IntPtr> _assemblyCache =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private static int _cacheStamp;
        private static int _cacheTick;
        private const int CacheTtlMs = 10000;

        [ThreadStatic] private static bool _attachedByUs;

        // =====================================================================
        // 状态查询
        // =====================================================================

        /// <summary>最近一次失败的描述(null = 无错误)。等价原生侧的 <c>g_last_error</c>。</summary>
        public static string LastError
        {
            get { return Volatile.Read(ref _lastError); }
        }

        /// <summary>
        /// IL2CPP 互操作是否可用: GameAssembly.dll 已加载 <b>且</b> 必需导出已全部解析。
        /// 这是"能不能安全调用"的唯一判据; 未就绪时所有 API 都返回 null/false。
        /// </summary>
        public static bool IsInitialized()
        {
            var t = EnsureTable();
            return t != null && t.Complete;
        }

        /// <summary>GameAssembly.dll 是否已在进程内(即使导出没解析全)。</summary>
        public static bool IsGameAssemblyLoaded()
        {
            var t = EnsureTable();
            return t != null && t.Module != IntPtr.Zero;
        }

        /// <summary>
        /// 当前线程是否已挂到 IL2CPP 域上。
        ///
        /// 判定顺序: 有 <c>il2cpp_thread_current</c> 就用它(权威);
        /// 没有则退化为"本类在本线程调用过 AttachCurrentThread"或"当前是 Unity 主线程"
        /// (主线程必然已挂接)。退化判定在文档中已注明, 不要据此做危险假设。
        /// </summary>
        public static bool IsAttached()
        {
            try
            {
                if (_attachedByUs) return true;

                var t = EnsureTable();
                if (t == null || !t.Complete) return false;

                if (t.ThreadCurrent != null)
                {
                    IntPtr current = t.ThreadCurrent();
                    if (current != IntPtr.Zero) return true;
                }
                else if (MainThread.IsMainThread)
                {
                    // 退化路径: 主线程一定是挂接过的
                    return true;
                }
            }
            catch (Exception e)
            {
                SetError("IsAttached", e.Message);
            }
            return false;
        }

        /// <summary>
        /// 把当前线程挂到 IL2CPP 域上 —— 后台线程访问 IL2CPP 元数据前的必要步骤。
        /// 已挂接或未就绪时安全返回 false, 不抛异常。
        /// </summary>
        public static bool AttachCurrentThread()
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete)
                {
                    SetError("AttachCurrentThread", "IL2CPP 互操作未就绪");
                    return false;
                }

                IntPtr domain = t.DomainGet();
                if (domain == IntPtr.Zero)
                {
                    SetError("AttachCurrentThread", "domain 为空");
                    return false;
                }

                IntPtr thread = t.ThreadAttach(domain);
                if (thread == IntPtr.Zero)
                {
                    SetError("AttachCurrentThread", "il2cpp_thread_attach 返回空");
                    return false;
                }

                _attachedByUs = true;
                ClearError();
                return true;
            }
            catch (Exception e)
            {
                SetError("AttachCurrentThread", e.Message);
                return false;
            }
        }

        /// <summary>IL2CPP 域句柄(未就绪返回 IntPtr.Zero)。</summary>
        public static IntPtr GetDomain()
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete) return IntPtr.Zero;
                return t.DomainGet();
            }
            catch (Exception e)
            {
                SetError("GetDomain", e.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>corlib 的 image 句柄(未就绪返回 IntPtr.Zero)。</summary>
        public static IntPtr GetCorlibImage()
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete) return IntPtr.Zero;
                return t.GetCorlib();
            }
            catch (Exception e)
            {
                SetError("GetCorlibImage", e.Message);
                return IntPtr.Zero;
            }
        }

        // =====================================================================
        // 程序集 / 类型 / 方法
        // =====================================================================

        /// <summary>枚举域内全部程序集 image 名(失败返回空数组)。</summary>
        public static string[] ListAssemblies()
        {
            var names = new List<string>(64);
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete)
                {
                    SetError("ListAssemblies", "IL2CPP 互操作未就绪");
                    return names.ToArray();
                }

                IntPtr domain = t.DomainGet();
                if (domain == IntPtr.Zero) { SetError("ListAssemblies", "domain 为空"); return names.ToArray(); }

                IntPtr count;
                IntPtr array = t.DomainGetAssemblies(domain, out count);
                if (array == IntPtr.Zero) { SetError("ListAssemblies", "domain_get_assemblies 返回空"); return names.ToArray(); }

                long n = count.ToInt64();
                for (long i = 0; i < n; i++)
                {
                    IntPtr assembly = Marshal.ReadIntPtr(array, (int)(i * IntPtr.Size));
                    if (assembly == IntPtr.Zero) continue;
                    IntPtr image = t.AssemblyGetImage(assembly);
                    if (image == IntPtr.Zero) continue;
                    string name = PtrToAnsi(t.ImageGetName(image));
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }

                ClearError();
            }
            catch (Exception e)
            {
                SetError("ListAssemblies", e.Message);
            }
            return names.ToArray();
        }

        /// <summary>
        /// 按 image 名查找程序集(如 "AstralParty.Runtime" / "mscorlib")。
        /// 结果带 10 秒缓存, 场景切换后可 <see cref="InvalidateCache"/> 立即刷新。
        /// </summary>
        public static IntPtr FindAssembly(string imageName)
        {
            if (string.IsNullOrEmpty(imageName)) return IntPtr.Zero;

            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete)
                {
                    SetError("FindAssembly", "IL2CPP 互操作未就绪");
                    return IntPtr.Zero;
                }

                lock (_lock)
                {
                    if (_cacheTick != 0 && Environment.TickCount - _cacheTick > CacheTtlMs)
                        _assemblyCache.Clear();
                    _cacheTick = Environment.TickCount;

                    IntPtr cached;
                    if (_assemblyCache.TryGetValue(imageName, out cached)) return cached;

                    IntPtr domain = t.DomainGet();
                    if (domain == IntPtr.Zero) { SetError("FindAssembly", "domain 为空"); return IntPtr.Zero; }

                    IntPtr count;
                    IntPtr array = t.DomainGetAssemblies(domain, out count);
                    if (array == IntPtr.Zero) { SetError("FindAssembly", "domain_get_assemblies 返回空"); return IntPtr.Zero; }

                    long n = count.ToInt64();
                    for (long i = 0; i < n; i++)
                    {
                        IntPtr assembly = Marshal.ReadIntPtr(array, (int)(i * IntPtr.Size));
                        if (assembly == IntPtr.Zero) continue;
                        IntPtr image = t.AssemblyGetImage(assembly);
                        if (image == IntPtr.Zero) continue;
                        string name = PtrToAnsi(t.ImageGetName(image));
                        if (string.IsNullOrEmpty(name)) continue;

                        _assemblyCache[name] = assembly;
                        if (string.Equals(name, imageName, StringComparison.OrdinalIgnoreCase))
                        {
                            ClearError();
                            return assembly;
                        }
                        // image 名可能带 .dll 后缀, 也接受"去掉后缀"的匹配
                        if (string.Equals(StripExtension(name), imageName, StringComparison.OrdinalIgnoreCase))
                        {
                            ClearError();
                            _assemblyCache[imageName] = assembly;
                            return assembly;
                        }
                    }
                }

                SetError("FindAssembly", "未找到程序集: " + imageName);
            }
            catch (Exception e)
            {
                SetError("FindAssembly", e.Message);
            }
            return IntPtr.Zero;
        }

        /// <summary>程序集 → image 句柄。</summary>
        public static IntPtr GetImage(IntPtr assembly)
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete || assembly == IntPtr.Zero) return IntPtr.Zero;
                return t.AssemblyGetImage(assembly);
            }
            catch (Exception e)
            {
                SetError("GetImage", e.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>按程序集名 + 命名空间 + 类名查找类(等价 il2cpp_class_from_name)。</summary>
        public static IntPtr GetClass(string imageName, string ns, string className)
        {
            IntPtr assembly = FindAssembly(imageName);
            if (assembly == IntPtr.Zero) return IntPtr.Zero;
            return GetClass(GetImage(assembly), ns, className);
        }

        /// <summary>按 image 句柄 + 命名空间 + 类名查找类。</summary>
        public static IntPtr GetClass(IntPtr image, string ns, string className)
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete) { SetError("GetClass", "IL2CPP 互操作未就绪"); return IntPtr.Zero; }
                if (image == IntPtr.Zero) { SetError("GetClass", "image 为空"); return IntPtr.Zero; }
                if (string.IsNullOrEmpty(className)) { SetError("GetClass", "类名为空"); return IntPtr.Zero; }

                IntPtr klass = t.ClassFromName(image, ns ?? "", className);
                if (klass == IntPtr.Zero) { SetError("GetClass", "未找到类: " + (ns ?? "") + "." + className); return IntPtr.Zero; }
                ClearError();
                return klass;
            }
            catch (Exception e)
            {
                SetError("GetClass", e.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>按类 + 方法名 + 参数个数查找方法。</summary>
        public static IntPtr GetMethod(IntPtr klass, string methodName, int paramCount = 0)
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete) { SetError("GetMethod", "IL2CPP 互操作未就绪"); return IntPtr.Zero; }
                if (klass == IntPtr.Zero) { SetError("GetMethod", "klass 为空"); return IntPtr.Zero; }
                if (string.IsNullOrEmpty(methodName)) { SetError("GetMethod", "方法名为空"); return IntPtr.Zero; }

                IntPtr method = t.ClassGetMethodFromName(klass, methodName, paramCount);
                if (method == IntPtr.Zero)
                {
                    SetError("GetMethod", "未找到方法: " + methodName + "/" + paramCount);
                    return IntPtr.Zero;
                }
                ClearError();
                return method;
            }
            catch (Exception e)
            {
                SetError("GetMethod", e.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>方法名(失败返回 null)。</summary>
        public static string GetMethodName(IntPtr method)
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete || method == IntPtr.Zero) return null;
                return PtrToAnsi(t.MethodGetName(method));
            }
            catch (Exception e)
            {
                SetError("GetMethodName", e.Message);
                return null;
            }
        }

        /// <summary>方法参数个数(失败返回 -1)。</summary>
        public static int GetMethodParamCount(IntPtr method)
        {
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete || method == IntPtr.Zero) return -1;
                return (int)t.MethodGetParamCount(method);
            }
            catch (Exception e)
            {
                SetError("GetMethodParamCount", e.Message);
                return -1;
            }
        }

        // =====================================================================
        // 调用
        // =====================================================================

        /// <summary>
        /// 调用一个 il2cpp 方法 —— il2cpp_safe.h 的 <c>safe_invoke_static</c> 的托管版本。
        ///
        /// <para>
        /// <paramref name="instance"/> 传 <see cref="IntPtr.Zero"/> 表示静态方法。
        /// <paramref name="args"/> 必须是 il2cpp 能接受的托管指针数组(值类型需自行装箱后取指针);
        /// 本方法<b>不做</b>参数编组 —— 拿不准时请走 <see cref="RuntimeAssemblyService"/> 的托管反射路径。
        /// </para>
        /// </summary>
        /// <returns>托管返回值(可能为空); 失败返回 <see cref="IntPtr.Zero"/> 且 <see cref="LastError"/> 有值。</returns>
        public static IntPtr Invoke(IntPtr method, IntPtr instance, params IntPtr[] args)
        {
            string error;
            return Invoke(method, instance, args, out error);
        }

        /// <summary><see cref="Invoke(IntPtr,IntPtr,IntPtr[])"/> 的带错误输出版本。</summary>
        public static IntPtr Invoke(IntPtr method, IntPtr instance, IntPtr[] args, out string error)
        {
            error = null;
            try
            {
                var t = EnsureTable();
                if (t == null || !t.Complete) { error = Fail("Invoke", "IL2CPP 互操作未就绪"); return IntPtr.Zero; }
                if (method == IntPtr.Zero) { error = Fail("Invoke", "method 为空"); return IntPtr.Zero; }

                // 后台线程: 先挂接再调用(否则 il2cpp 内部 TLS 未初始化)
                if (!MainThread.IsMainThread && !IsAttached())
                {
                    if (!AttachCurrentThread())
                    {
                        error = Fail("Invoke", "线程未挂接到 IL2CPP 域");
                        return IntPtr.Zero;
                    }
                }

                IntPtr exception;
                IntPtr result = t.RuntimeInvoke(method, instance, args, out exception);
                if (exception != IntPtr.Zero)
                {
                    error = Fail("Invoke", TranslateException(t, exception));
                    return IntPtr.Zero;
                }

                ClearError();
                return result;
            }
            catch (Exception e)
            {
                error = Fail("Invoke", e.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>把 il2cpp 托管字符串转成 C# 字符串(失败返回 null)。</summary>
        public static string GetManagedString(IntPtr il2cppString)
        {
            try
            {
                var t = EnsureTable();
                if (t == null || t.StringChars == null || il2cppString == IntPtr.Zero) return null;
                IntPtr chars = t.StringChars(il2cppString);
                if (chars == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(chars);
            }
            catch (Exception e)
            {
                SetError("GetManagedString", e.Message);
                return null;
            }
        }

        /// <summary>
        /// 任意导出地址(给 <c>ap_*</c> 自定义导出和未来扩展留的口子;
        /// 拿到裸指针后如何调用由调用方负责, SDK 不做保证)。
        /// </summary>
        public static IntPtr GetExport(string exportName)
        {
            try
            {
                if (string.IsNullOrEmpty(exportName)) return IntPtr.Zero;
                IntPtr module = EnsureModule();
                if (module == IntPtr.Zero) return IntPtr.Zero;
                IntPtr fn = Native.GetProcAddress(module, exportName);
                if (fn == IntPtr.Zero) SetError("GetExport", "未找到导出: " + exportName);
                return fn;
            }
            catch (Exception e)
            {
                SetError("GetExport", e.Message);
                return IntPtr.Zero;
            }
        }

        // =====================================================================
        // 缓存 / 诊断
        // =====================================================================

        /// <summary>清空程序集缓存(场景切换后调用; 加载了新程序集时也应变动)。</summary>
        public static void InvalidateCache()
        {
            lock (_lock)
            {
                _assemblyCache.Clear();
                _cacheTick = 0;
                _cacheStamp++;
            }
        }

        /// <summary>缓存版本号(每次失效 +1; 诊断/测试用)。</summary>
        public static int CacheStamp
        {
            get { lock (_lock) { return _cacheStamp; } }
        }

        /// <summary>单行状态描述, 供诊断输出。</summary>
        public static string Describe()
        {
            try
            {
                var t = EnsureTable();
                if (t == null || t.Module == IntPtr.Zero)
                    return "il2cpp-interop=不可用(GameAssembly.dll 未找到" + (_failReason != null ? ": " + _failReason : "") + ")";

                if (!t.Complete)
                    return "il2cpp-interop=不完整(必需导出缺失: " + (_failReason ?? "?") + ")";

                IntPtr domain = t.DomainGet();
                return "il2cpp-interop=就绪 domain=0x" + domain.ToInt64().ToString("X") +
                       " thread_current=" + (t.ThreadCurrent != null ? "可用" : "缺失") +
                       " assemblies=" + ListAssemblies().Length +
                       " attached=" + IsAttached();
            }
            catch (Exception e)
            {
                return "il2cpp-interop=异常(" + e.Message + ")";
            }
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private static Table EnsureTable()
        {
            lock (_lock)
            {
                if (_table != null) return _table;
                if (_resolveAttempted) return _table;   // 解析过且失败: 不再反复尝试
                _resolveAttempted = true;
                _table = Resolve();
                return _table;
            }
        }

        private static IntPtr EnsureModule()
        {
            lock (_lock)
            {
                if (_table != null && _table.Module != IntPtr.Zero) return _table.Module;
                try
                {
                    IntPtr module = Native.GetModuleHandleW("GameAssembly.dll");
                    if (module == IntPtr.Zero)
                        _failReason = "GetModuleHandleW 失败(err=" + Marshal.GetLastWin32Error() + ")";
                    return module;
                }
                catch (Exception e)
                {
                    _failReason = e.Message;
                    return IntPtr.Zero;
                }
            }
        }

        /// <summary>一次性解析全部导出。任何一项取不到都只是置空, 由 <see cref="Table.Complete"/> 兜底。</summary>
        private static Table Resolve()
        {
            var t = new Table();
            try
            {
                t.Module = EnsureModule();
                if (t.Module == IntPtr.Zero)
                {
                    SdkLog.Warn("IL2CPP", "GameAssembly.dll 未加载, IL2CPP 互操作停用 (" + _failReason + ")");
                    return t;
                }

                t.DomainGet = Bind<Fn_DomainGet>(t.Module, "il2cpp_domain_get");
                t.AssemblyGetImage = Bind<Fn_AssemblyGetImage>(t.Module, "il2cpp_assembly_get_image");
                t.ImageGetAssembly = Bind<Fn_ImageGetAssembly>(t.Module, "il2cpp_image_get_assembly");
                t.ClassFromName = Bind<Fn_ClassFromName>(t.Module, "il2cpp_class_from_name");
                t.ClassGetMethodFromName = Bind<Fn_ClassGetMethodFromName>(t.Module, "il2cpp_class_get_method_from_name");
                t.ClassGetMethods = Bind<Fn_ClassGetMethods>(t.Module, "il2cpp_class_get_methods");
                t.MethodGetName = Bind<Fn_MethodGetName>(t.Module, "il2cpp_method_get_name");
                t.MethodGetParam = Bind<Fn_MethodGetParam>(t.Module, "il2cpp_method_get_param");
                t.MethodGetParamCount = Bind<Fn_MethodGetParamCount>(t.Module, "il2cpp_method_get_param_count");
                t.RuntimeInvoke = Bind<Fn_RuntimeInvoke>(t.Module, "il2cpp_runtime_invoke");
                t.ThreadAttach = Bind<Fn_ThreadAttach>(t.Module, "il2cpp_thread_attach");
                t.DomainGetAssemblies = Bind<Fn_DomainGetAssemblies>(t.Module, "il2cpp_domain_get_assemblies");
                t.ImageGetName = Bind<Fn_ImageGetName>(t.Module, "il2cpp_image_get_name");
                t.GetCorlib = Bind<Fn_GetCorlib>(t.Module, "il2cpp_get_corlib");
                t.TypeGetName = Bind<Fn_TypeGetName>(t.Module, "il2cpp_type_get_name");
                t.ArrayClassGet = Bind<Fn_ArrayClassGet>(t.Module, "il2cpp_array_class_get");
                t.ArrayNew = Bind<Fn_ArrayNew>(t.Module, "il2cpp_array_new");
                t.ArrayObjectHeaderSize = Bind<Fn_ArrayObjectHeaderSize>(t.Module, "il2cpp_array_object_header_size");

                // 可选导出: 缺失不影响可用性
                t.ThreadCurrent = Bind<Fn_ThreadCurrent>(t.Module, "il2cpp_thread_current");
                t.ExceptionGetMessage = Bind<Fn_ExceptionGetMessage>(t.Module, "il2cpp_exception_get_message");
                t.StringChars = Bind<Fn_StringChars>(t.Module, "il2cpp_string_chars");

                if (!t.Complete)
                {
                    _failReason = DescribeMissing(t);
                    SdkLog.Warn("IL2CPP", "必需导出缺失, IL2CPP 互操作停用: " + _failReason);
                }
                else
                {
                    SdkLog.Info("IL2CPP", "互操作层就绪(GameAssembly.dll 导出已解析, 可选 thread_current=" +
                                         (t.ThreadCurrent != null ? "有" : "无") + ")");
                }
            }
            catch (Exception e)
            {
                _failReason = e.Message;
                SdkLog.Error("IL2CPP", "解析 GameAssembly.dll 导出失败: " + e.Message);
            }
            return t;
        }

        private static T Bind<T>(IntPtr module, string name) where T : class
        {
            try
            {
                IntPtr fn = Native.GetProcAddress(module, name);
                if (fn == IntPtr.Zero) return null;
                return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
            }
            catch (Exception e)
            {
                SdkLog.Debug("IL2CPP", "绑定导出失败 " + name + ": " + e.Message);
                return null;
            }
        }

        private static string DescribeMissing(Table t)
        {
            var missing = new List<string>(4);
            if (t.DomainGet == null) missing.Add("il2cpp_domain_get");
            if (t.AssemblyGetImage == null) missing.Add("il2cpp_assembly_get_image");
            if (t.ImageGetAssembly == null) missing.Add("il2cpp_image_get_assembly");
            if (t.ClassFromName == null) missing.Add("il2cpp_class_from_name");
            if (t.ClassGetMethodFromName == null) missing.Add("il2cpp_class_get_method_from_name");
            if (t.ClassGetMethods == null) missing.Add("il2cpp_class_get_methods");
            if (t.MethodGetName == null) missing.Add("il2cpp_method_get_name");
            if (t.MethodGetParam == null) missing.Add("il2cpp_method_get_param");
            if (t.MethodGetParamCount == null) missing.Add("il2cpp_method_get_param_count");
            if (t.RuntimeInvoke == null) missing.Add("il2cpp_runtime_invoke");
            if (t.ThreadAttach == null) missing.Add("il2cpp_thread_attach");
            if (t.DomainGetAssemblies == null) missing.Add("il2cpp_domain_get_assemblies");
            if (t.ImageGetName == null) missing.Add("il2cpp_image_get_name");
            if (t.GetCorlib == null) missing.Add("il2cpp_get_corlib");
            if (t.TypeGetName == null) missing.Add("il2cpp_type_get_name");
            if (t.ArrayClassGet == null) missing.Add("il2cpp_array_class_get");
            if (t.ArrayNew == null) missing.Add("il2cpp_array_new");
            if (t.ArrayObjectHeaderSize == null) missing.Add("il2cpp_array_object_header_size");
            return string.Join(", ", missing.ToArray());
        }

        /// <summary>把 il2cpp 异常转成 "ClassName: Message" 形式的可读文本。</summary>
        private static string TranslateException(Table t, IntPtr exception)
        {
            try
            {
                if (t.ExceptionGetMessage == null || t.StringChars == null) return "托管异常(无法取消息)";
                IntPtr message = t.ExceptionGetMessage(exception);
                if (message == IntPtr.Zero) return "托管异常(消息为空)";
                IntPtr chars = t.StringChars(message);
                if (chars == IntPtr.Zero) return "托管异常(字符串为空)";
                string text = Marshal.PtrToStringUni(chars);
                return string.IsNullOrEmpty(text) ? "托管异常" : text;
            }
            catch (Exception e)
            {
                return "托管异常(转译失败: " + e.Message + ")";
            }
        }

        private static string PtrToAnsi(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringAnsi(p); }
            catch { return null; }
        }

        private static string StripExtension(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static void SetError(string where, string message)
        {
            Volatile.Write(ref _lastError, where + ": " + message);
        }

        private static void ClearError()
        {
            Volatile.Write(ref _lastError, null);
        }

        private static string Fail(string where, string message)
        {
            SetError(where, message);
            return where + ": " + message;
        }
    }
}

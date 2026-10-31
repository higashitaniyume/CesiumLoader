// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 阶段3 hook + 默认返回值工厂类型/模式 + 共享小工具 ----------
// ---------- hook 5: System.Nullable`1<Lobby>::get_HasValue -> 恒 false (阶段3) ----------
//
// 为什么是这个方法: UICom_CreateRoom.RequestCreateRoom(热更程序集, HybridCLR 解释执行)里
//     Lobby? val = await SteamMatchmaking.CreateLobbyAsync(4);
//     if (val.HasValue) { ... ((Lobby)(ref value)).SetPublic(); ... }   // 炸在这里
// 阶段2 已经让 CreateLobbyAsync 返回"已完成的空 Task", 但实测 val.HasValue 仍为 true ——
// 安装期探针里 get_Result() 返回非空装箱对象, 而"空 Nullable"装箱本应得 NULL
// (il2cpp_value_box 同样输入确实返回 NULL), 说明 il2cpp_runtime_invoke 把 Nullable<Lobby>
// 参数编组进 Task<T>..ctor(T) 时 hasValue 落成了非零。
//
// 修法(不再碰 Task 的构造): 热更代码的 val.HasValue 是一次真实的方法调用, 会 call 到
// AOT 方法 System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue() —— 直接把它换成恒 false,
// steamLobbyId 保持 0(协议里"无 Steam 大厅"合法值), RequestCreateC2S 正常发出。
//
// 签名: get_HasValue 是值类型实例方法, this 是**指向 Nullable 值的指针**(AOT ABI 值类型按地址传)。
// 返回值 bool: 寄存器返回(AL), 返回 0 即可。
// 第 2 个参数: il2cpp 的**共享泛型代码**会把运行时泛型上下文的 MethodInfo 作为隐藏参数传入。
//   本模块不依赖它(恒返回 false), 但**只读寄存器、不解引用**地记一次日志 —— 它能直接说明
//   "这个方法到底是不是所有 Nullable<T> 共享的同一份代码"(见 probe_nullable_generic_sharing)。
using Fn_HasValue = bool (*)(void* this_ptr, void* generic_ctx);

Fn_HasValue g_real_hasvalue = nullptr;   // MinHook trampoline(保留备用)
void* g_hv_target = nullptr;             // get_HasValue 的原始 methodPointer
void* g_hv_method = nullptr;             // get_HasValue 的 MethodInfo*(Nullable<Lobby> 实例)
bool g_hv_hooked = false;
std::atomic<long> g_hv_hits{ 0 };

bool Hook_NullableHasValue(void* this_ptr, void* generic_ctx)
{
    if (g_hv_hits.fetch_add(1, std::memory_order_relaxed) == 0)
    {
        const bool same = (generic_ctx != nullptr && generic_ctx == g_hv_method);
        log_line("[steamhack] Nullable<Lobby>.get_HasValue intercepted on first hit: this=" +
                 cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(this_ptr)) +
                 " 2nd register arg (hidden context of shared generic code; raw value only, not dereferenced)=" +
                 cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(generic_ctx)) +
                 " matches this instantiation's MethodInfo(" + cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(g_hv_method)) +
                 ") match=" + (same ? "yes" : "no") +
                 " -> always returns false (any Lobby? counts as no value; game keeps LobbyId 0)");
    }
    return false;
}

// 造"默认返回值"的方式
enum TaskFactoryKind
{
    TF_NONE = 0,
    TF_FROM_RESULT,       // cls.FromResult(1)               —— 规格要求的主路径
    TF_CTOR_OBJECT_NEW,   // object_new(cls) + cls..ctor(1)  —— 等价兜底
    TF_COMPLETED_TASK,    // cls.get_CompletedTask()          —— 仅非泛型 Task 可用
};

// =====================================================================================
// 方案C: Task<T>..ctor(T) 的调用方式(config: steamBypassTaskCtorMode)
// =====================================================================================
//
// 实测根因(已定位, 不再调研): il2cpp_runtime_invoke 给 Task<T>..ctor(Nullable<Lobby>) 传
// 值类型参数时**编组不正确** —— 传进去的 Nullable 的 hasValue 落成了非零。证据: 安装期探针里
// il2cpp_field_get_value_object(m_result, task) 返回的是**非空**装箱对象, 而"空 Nullable"
// 按 .NET 语义装箱应得 NULL(il2cpp_value_box 在同样的零初始化输入下确实返回了 NULL)。
// 于是 await 回来后 val.HasValue 被判成 true -> UICom_CreateRoom.cs:136 SetPublic() NRE。
//
// 方案C: **不再走 runtime_invoke**, 用 *(void**)ctorMethodInfo 拿到 methodPointer 直接当原生
// 函数调用。Windows x64 ABI 下 >8 字节的结构体参数按引用(指针)传递, IL2CPP 生成的 AOT 代码
// 遵循同一套 ABI, 所以
//     void (*)(void* thisObj, void* pArg)
// 正好对应生成的 (Task<T>* this, Nullable<Lobby> result)。Nullable<Lobby> = bool hasValue +
// Lobby(内含 SteamId, 8B) = 9B -> 补齐 16B -> 走引用。256B 全零栈缓冲 = "hasValue=false"。
enum TaskCtorMode
{
    CTOR_MODE_AUTO = 0,   // 先 direct, 探针校验失败则自动退回 invoke(默认)
    CTOR_MODE_DIRECT = 1, // 只用 direct(原生 ABI); 失败即不挂钩
    CTOR_MODE_INVOKE = 2, // 只用 il2cpp_runtime_invoke(旧行为)
};

TaskCtorMode g_ctor_mode = CTOR_MODE_AUTO;
const char* g_ctor_mode_name = "auto";

// 解析配置字符串; 非法取值按 AUTO 处理并经 out_unknown 告知调用方(由调用方记日志)。
TaskCtorMode parse_ctor_mode(const std::string& s, bool* out_unknown)
{
    if (out_unknown) *out_unknown = false;
    if (s == "direct") return CTOR_MODE_DIRECT;
    if (s == "invoke") return CTOR_MODE_INVOKE;
    if (s.empty() || s == "auto") return CTOR_MODE_AUTO;
    if (out_unknown) *out_unknown = true;
    return CTOR_MODE_AUTO;
}

const char* ctor_mode_name(TaskCtorMode m)
{
    return (m == CTOR_MODE_DIRECT) ? "direct" : (m == CTOR_MODE_INVOKE) ? "invoke" : "auto";
}

// 探测成功时实际采用的路径(日志与运行期分发共用)
enum CtorPath
{
    CPATH_UNSET = 0,
    CPATH_DIRECT = 1,   // 原生 ABI 直调 .ctor methodPointer(方案C)
    CPATH_INVOKE = 2,   // il2cpp_runtime_invoke(旧路径)
};

const char* ctor_path_name(int p)
{
    return (p == CPATH_DIRECT) ? "direct (native ABI direct ctor call, bypassing runtime_invoke marshaling)"
                               : "invoke(il2cpp_runtime_invoke)";
}

// 单个被挂钩方法的"默认返回值工厂": 安装期解析 + 探测一次, 命中路径只做一次缓存好的调用。
struct TaskFactory
{
    const char* label = "?";
    void* hooked_method = nullptr;   // MethodInfo*(键)
    void* ret_type = nullptr;        // Il2CppType*(被挂钩方法的返回值类型)
    void* ret_class = nullptr;       // Il2CppClass*(该 Il2CppType 对应的类, 即 Task<T>)
    std::string ret_type_name = "?";
    void* factory = nullptr;         // 用来造返回值的 MethodInfo*
    TaskFactoryKind kind = TF_NONE;
    bool resolved = false;
    bool ok = false;

    // ---- 方案C 新增 ----
    void* ctor_ptr = nullptr;        // .ctor(1) 的 methodPointer(原生函数入口, 直调用)
    int   ctor_path = CPATH_UNSET;   // 探测成功后实际采用的路径(CPATH_*): 运行期按它分发
    std::atomic<long> runtime_logged{ 0 }; // 运行期 direct 路径的详细日志只记一次

    // ---- 阶段3 新增: 工厂第一个参数(值类型)的类 ----
    // 为了装箱, 安装期已经从工厂参数解析出 pcls = class_from_system_type(type_get_object(
    // method_get_param(ctorMi, 0))), 这里把它**留下来复用**, 不再重复解析:
    // 它就是 System.Nullable`1<Steamworks.Data.Lobby>, 是 get_HasValue 挂钩和 hasValue
    // 字段诊断的入口。
    void* param_class = nullptr;                  // Il2CppClass*(Nullable<Lobby>)
    std::string param_class_name = "(unresolved)";     // 类名(日志: 要能确认是 Nullable`1)
    bool param_is_valuetype = false;
    bool param_is_nullable = false;               // 类名/类型名里含 "Nullable"
    void* hasvalue_method = nullptr;              // Nullable<Lobby>.get_HasValue 的 MethodInfo*
    bool hasvalue_resolved = false;               // 已尝试解析(不等于成功)
    bool hasvalue_ok = false;                     // 真的挂上了

    std::atomic<long> failures{ 0 }; // 只记首次失败, 不刷屏
    std::atomic<long> box_logged{ 0 }; // 装箱路径只记一次(安装期那次必定记录), 运行期不刷屏
};

TaskFactory g_factory_cl;   // CreateLobbyAsync
TaskFactory g_factory_jl;   // JoinLobbyAsync

// steamhack_install 时保存一份函数表副本: 两个 hook 处理器需要它。
// (loader.cpp 的 g_safe_il2cpp 是静态的、生命周期足够, 这里复制一份更稳妥。)
cesium_safe::il2cpp_tbl g_t;
bool g_t_ready = false;

// 读 MethodInfo 的第一个字段 = methodPointer。IL2CPP 的稳定布局, 阶段1 的 Awake hook 也用它。
void* method_pointer_of(const void* mi)
{
    return mi ? *reinterpret_cast<void* const*>(mi) : nullptr;
}

std::string cstr_or(const char* s, const char* def)
{
    return s ? std::string(s) : std::string(def);
}

std::string class_name_of(const cesium_safe::il2cpp_tbl& t, void* cls)
{
    if (!cls) return "(null)";
    if (!t.class_get_name) return "(class_get_name missing)";
    return cstr_or(t.class_get_name(cls), "(null)");
}

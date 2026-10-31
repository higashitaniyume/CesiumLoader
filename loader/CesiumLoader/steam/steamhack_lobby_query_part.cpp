// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 阶段5: 预建 Task 状态 + RequestAsync 处理器 + POD 调用 ----------

// =====================================================================================
// 阶段5: LobbyQuery.RequestAsync() -> 结果为空 Lobby[] 的已完成 Task<Lobby[]>
// =====================================================================================
//
// 故障(实机实测, 6 条 NRE):
//     NullReferenceException
//     GameLogic.RoomLogic.OnExitRoomS2CServerCallBack (party.protocol.ExitRoomS2C model, ...)
//     Core.Net.NetManager.Update ()
//     Steamworks.Data.LobbyQuery.RequestAsync ()          <- NRE 真正抛出点
// 对应 decomp_full\GameLogic\RoomLogic.cs:381-401(退房/解散)与 :545-573(被踢)同构:
//     if (model.Dissolve || model.PlayerId == self)                       // :381
//     {
//         LobbyQuery lobbyList = SteamMatchmaking.LobbyList;              // :383
//         Lobby[] array = await ((LobbyQuery)(ref lobbyList)).RequestAsync();  // :384 <- 这里抛
//         if (array != null && array.Length > 0) { ...Leave()... }        // :385
//     }
//     roomController.UpdateRoomByExit(...);                               // :398 <- 被跳过
//     if (self || model.Dissolve) ClearRoomInfo();                        // :401 <- 被跳过
//
// ★ 对文档里那行"未实测"的更正(有实测数据了, 据此更正):
//   此前文档写的是"`SteamMatchmaking.LobbyList` 属性/查询无判空, Steam 未初始化时行为**未实测**,
//   最坏是访问违例"。实测把这一点确定了: **NRE 抛在 await 的 RequestAsync() 上, 不是 LobbyList
//   取值本身**(栈顶就是 Steamworks.Data.LobbyQuery.RequestAsync)。危害也不是"最坏是访问违例",
//   而是 **await 之后的代码全部不执行** —— 本地房间状态不清、被踢提示不弹、不返回房间列表页。
//
// 修法: 挂钩 RequestAsync() -> 恒返回一个**预建好的、结果为长度 0 的 Lobby[] 的已完成 Task<Lobby[]>**。
//   于是 array 是长度 0 的数组 -> (array != null && array.Length > 0) 为 false -> 跳过 Steam 大厅
//   清理 -> :398 / :401 / :567-573 正常执行。
//
// 影响面为零: 全仓 grep 确认全游戏**只有这 2 处**调用 LobbyQuery.RequestAsync
//   (RoomLogic.cs:384 与 :554), 所以挂钩这一个方法即可, 对其它 Steam 行为零影响。
//
// 与阶段2/方案C 的关系:
//   阶段2 处理的是 `Task<Lobby?>` —— 泛型参数是 **Nullable<Lobby> 值类型**, 于是踩到
//   il2cpp_runtime_invoke 的编组 bug, 需要方案C 的 direct ABI + hasValue 规避。
//   阶段5 的泛型参数是 **引用类型 Lobby[]**, 不涉及 Nullable 装箱: 要传的实参就是
//   "一个长度 0 的 Lobby[] 数组对象指针"。所以这里**复用方案C 已验证的 direct 原生 ABI 直调
//   ctor 机制**(pod_call_lobby_query_ctor 与 pod_call_task_ctor_direct 是同一套调用约定),
//   只把"指向全零栈缓冲区的指针"换成"数组对象指针"。
//
// 失败一律 fail-safe(**只记日志, 不挂钩**): 解析不到类/方法、methodPointer 为空、ctor 参数被
//   判定为值类型、探针校验不通过、MinHook 失败 —— 都只是"回到当前那 6 条 NRE",
//   绝不让安装期崩游戏(前面几轮的硬要求)。

// RequestAsync 是 LobbyQuery 的实例方法且无参数, 原生签名 = (LobbyQuery* __this, const MethodInfo* method)。
// 第一个参数是指向该值的指针 —— 与"LobbyQuery 到底是 struct 还是 class"无关(两种情况下都是一个指针),
// 本模块**不解引用它**。多余/缺失的寄存器参数对"恒返回同一个指针"的逻辑没有任何影响。
using Fn_LobbyQueryRequestAsync = void* (*)(void* this_ptr, void* method_info);

Fn_LobbyQueryRequestAsync g_real_lq_request = nullptr;   // MinHook trampoline(保留备用)
void* g_lq_target = nullptr;      // RequestAsync 的原始 methodPointer
void* g_lq_method = nullptr;      // RequestAsync 的 MethodInfo*(诊断/日志)
bool  g_lq_hooked = false;
std::atomic<long> g_lq_hits{ 0 };

// ---- GC root(设计说明, 重要) ----
// 这个 Task 只构造**一次**, 之后每一次 RequestAsync 调用都返回**同一个对象**(见下)。
// 它必须是 GC root, 否则被回收之后再被我们返回给托管代码就是 use-after-free(运行期崩溃)。
//   * g_lq_task / g_lq_empty_array 是**模块级全局变量**(静态存储期) —— Boehm 会扫描已加载模块的
//     可写数据段, 正常情况下这足以当根。
//   * **额外**再用 il2cpp_gchandle_new 显式注册成根(该导出存在时): "Boehm 到底扫不扫我们这张
//     DLL 的 .data"不是本模块能保证的事, 而一旦不是, 后果是崩溃而不是"功能失效"。显式句柄是
//     确定性的保证。导出缺失时只用全局变量, 并在日志里如实标注。
// 为什么复用是安全的: Task<TResult> 是不可变对象, get_Result() 恒返回同一个空数组; 代价只是常驻
// 一个小对象对(GC 永不回收它 —— 这是刻意的)。
void* g_lq_task = nullptr;              // Task<Lobby[]>(已完成, m_result = 长度 0 的 Lobby[])
void* g_lq_empty_array = nullptr;       // 长度 0 的 Lobby[](被上面的 Task 引用)
uint32_t g_lq_task_gchandle = 0;        // il2cpp_gchandle_new 的句柄(0 = 未持有/导出缺失/失败)
void* g_lq_task_class = nullptr;        // Task<Lobby[]> 的 Il2CppClass*(诊断)
void* g_lq_array_class = nullptr;       // Lobby[] 的 Il2CppClass*(探针的类型校验用)

// 恒返回**预建好的同一个** Task 对象; 该对象在安装期已构造 + 校验通过(g_lq_task 非空才会挂钩)。
void* Hook_LobbyQueryRequestAsync(void* this_ptr, void* method_info)
{
    // 只在首次拦截时记一行(原子计数, 不刷屏)。
    // **这行日志是否存在是判断本 hook 是否真的被调用的唯一依据** —— 阶段3 的教训:
    // "安装成功"只说明 MH_CreateHook 返回了 MH_OK, 并不代表它会被调用。
    if (g_lq_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] LobbyQuery.RequestAsync intercepted on first hit: this=" +
                 cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(this_ptr)) +
                 " (LobbyQuery is passed by reference; this module does not dereference) 2nd register=" +
                 cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(method_info)) +
                 " -> always returns the prebuilt Task<Lobby[]> (result = empty Lobby[]); "
                 "the game's array != null && array.Length > 0 is false -> Steam lobby cleanup is skipped, "
                 "UpdateRoomByExit / ClearRoomInfo after await run normally");
    return g_lq_task;
}

// Steamworks.NET 里 LobbyQuery / Lobby 的命名空间是 "Steamworks.Data"; 其余为兜底。
const char* kSteamworksDataNamespaces[] = { "Steamworks.Data", "Steamworks", "", "Steamworks.NET" };

// 在域内全部镜像里找 ns.className。label 只用于日志前缀。
// 与 resolve_lobby_class(方案B)同一套路, 但**独立成一份** —— 不改动方案B 那份已实机验证过的代码,
// 同时让日志前缀是 "阶段5" 而不是 "方案B", 便于排障。
void* resolve_steamworks_class(const cesium_safe::il2cpp_tbl& t, const char* label,
                               const char* class_name,
                               std::string* out_image, std::string* out_ns)
{
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",            (const void*)t.domain_get },
        { "domain_get_assemblies", (const void*)t.domain_get_assemblies },
        { "assembly_get_image",    (const void*)t.assembly_get_image },
        { "image_get_name",        (const void*)t.image_get_name },
        { "class_from_name",       (const void*)t.class_from_name },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line(std::string("[steamhack] ") + label + ": IL2CPP function table missing (" + missing +
                 ") - cannot resolve " + class_name + ", not hooked (fail-safe)");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line(std::string("[steamhack] ") + label + ": il2cpp domain is null - cannot resolve " + class_name);
        return nullptr;
    }
    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line(std::string("[steamhack] ") + label + ": domain_get_assemblies returned null");
        return nullptr;
    }

    void* chosen = nullptr;
    std::string chosen_image;
    std::string chosen_ns;
    std::string all;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        const std::string iname = cstr_or(t.image_get_name(image), "(null)");
        if (!all.empty()) all += ", ";
        all += iname;

        for (const char* ns : kSteamworksDataNamespaces)
        {
            void* cls = t.class_from_name(image, ns, class_name);
            if (!cls) continue;
            const bool is_vt = (t.class_is_valuetype ? t.class_is_valuetype(cls) : false);
            log_line(std::string("[steamhack] ") + label + ": class hit " + class_name + ": image=" + iname +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " className=" + class_name_of(t, cls) +
                     " isValueType=" + (is_vt ? "yes" : "no"));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line(std::string("[steamhack] ") + label + ": class not found " + class_name +
                 " (candidate ns: \"Steamworks.Data\" / \"Steamworks\" / global / \"Steamworks.NET\")");
        log_line(std::string("[steamhack] ") + label + ": domain assemblies " + std::to_string(count) + " items: " + all);
    }
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

// 【危险调用 #6】按原生 ABI 直调 Task<Lobby[]>::.ctor(Lobby[])。只允许 POD + __try 兜底。
//
// 与 pod_call_task_ctor_direct(方案C)是**同一套调用约定**, 唯一区别是第二个实参:
//   方案C: "指向 256B 全零栈缓冲区的指针" = 空 Nullable<Lobby>(值类型按引用传参);
//   阶段5: "长度 0 的 Lobby[] 对象指针"     = 引用类型, 指针本身就是实参。
// 两种情况在 Windows x64 ABI 下第二个寄存器参数都是一个指针(实参按指针大小传递), 所以调用方式一致。
// 第三个参数仍按 IL2CPP 生成代码的惯例补上 MethodInfo*:
//   void Task_1_ctor(Task_1_t* __this, T_t result, const MethodInfo* method)
struct PodLobbyQueryCtor
{
    void* object;            // 构造好的 Task 对象(nullptr = 失败)
    unsigned long seh_code;  // 捕获到的 SEH 异常码(0 = 未捕获)
    int   alloc_ok;          // il2cpp_object_new 是否成功
    int   called;            // 是否真的把 ctor 当原生函数调用过
    int   skipped;           // 1 = 未尝试(缺 object_new / ctor_ptr / task_cls)
    int   arg_was_null;      // 1 = 降级路径: 实参传的是 nullptr(而不是空数组)
};

void pod_call_lobby_query_ctor(void* object_new_fn, void* task_cls, void* ctor_ptr,
                               void* ctor_mi, void* arg, PodLobbyQueryCtor* out)
{
    out->object = nullptr;
    out->seh_code = 0;
    out->alloc_ok = 0;
    out->called = 0;
    out->skipped = 0;
    out->arg_was_null = (arg == nullptr) ? 1 : 0;

    if (!object_new_fn || !task_cls || !ctor_ptr)
    {
        out->skipped = 1;
        return;
    }

    __try
    {
        typedef void* (*ObjNewFn)(void*);
        void* obj = ((ObjNewFn)object_new_fn)(task_cls);
        if (!obj) return;
        out->alloc_ok = 1;

        // null 实参的语义是"结果为空引用": 游戏侧 array != null 为 false, 同样跳过 Steam 大厅清理。
        // 所以降级路径也是安全的(只是日志里会被明确标注)。
        typedef void (*CtorFn)(void* this_obj, void* p_arg, void* method_info);
        out->called = 1;
        ((CtorFn)ctor_ptr)(obj, arg, ctor_mi);

        out->object = obj;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // 捕获访问违例等 SEH: 只作废这一次调用, 进程继续(安装期 -> 不挂钩)。
        out->seh_code = GetExceptionCode();
        out->object = nullptr;
    }
}

// 【危险调用 #7】造一个长度 0 的元素数组。POD + __try。
// **注意参数语义**: il2cpp_array_new(klass, len) 的第 1 个参数是**元素类型**(Lobby),
// 不是数组类型(Lobby[]) —— 收数组类型的是 il2cpp_array_new_specific。传错会得到 Lobby[][]。
void pod_array_new_empty(void* array_new_fn, void* element_cls, void** out_arr, unsigned long* out_seh)
{
    *out_arr = nullptr;
    *out_seh = 0;
    if (!array_new_fn || !element_cls) return;

    __try
    {
        typedef void* (*ArrayNewFn)(void*, size_t);
        *out_arr = ((ArrayNewFn)array_new_fn)(element_cls, 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_arr = nullptr;
    }
}

// 【危险调用 #8】读 IL2CPP 数组的元素个数(探针校验 + 诊断)。POD + __try。
// 优先用**权威导出** il2cpp_array_length; 缺失时退化为按数组布局读 max_length:
//   x64 IL2CPP 数组 = Il2CppObject(16) + bounds(8) + max_length -> 数据起点 32;
//   il2cpp_array_object_header_size() 返回的正是"数据起点", max_length 紧邻它之前。
// 这里只读低 4 字节并与 0 比较 —— 无论 max_length 是 4 字节还是 8 字节, "是否等于 0" 都成立。
struct PodArrayLength
{
    int length;              // -1 = 读不到
    int source;              // 0 未读 / 1 il2cpp_array_length / 2 布局偏移
    unsigned long seh_code;  // 捕获到的 SEH 异常码(0 = 未捕获)
};

void pod_array_length(const cesium_safe::il2cpp_tbl* t, void* arr, PodArrayLength* out)
{
    out->length = -1;
    out->source = 0;
    out->seh_code = 0;
    if (!arr) return;

    __try
    {
        if (t->array_length)
        {
            out->length = (int)t->array_length(arr);
            out->source = 1;
            return;
        }
        if (t->array_object_header_size)
        {
            const size_t hs = t->array_object_header_size();
            if (hs >= 16)
            {
                uint32_t v = 0;
                memcpy(&v, reinterpret_cast<const unsigned char*>(arr) + hs - 8, sizeof(v));
                out->length = (int)v;
                out->source = 2;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out->seh_code = GetExceptionCode();
        out->length = -1;
        out->source = 0;
    }
}

// 【危险调用 #9】il2cpp_gchandle_new(obj, pinned=false): 把预建好的 Task 显式注册成 GC root。
// 返回 0 视为"未成功"(日志里如实标注), 那时只靠模块级全局变量作根。POD + __try。
void pod_gchandle_new(void* gchandle_new_fn, void* obj, uint32_t* out_handle, unsigned long* out_seh)
{
    *out_handle = 0;
    *out_seh = 0;
    if (!gchandle_new_fn || !obj) return;

    __try
    {
        typedef uint32_t (*GcHandleNewFn)(void*, bool);
        *out_handle = ((GcHandleNewFn)gchandle_new_fn)(obj, false);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_handle = 0;
    }
}

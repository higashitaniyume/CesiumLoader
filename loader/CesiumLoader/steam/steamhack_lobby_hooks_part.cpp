// Internal implementation fragment. Included by steamhack.cpp in source order.
void Hook_LobbySetPublic(void* /*self*/)
{
    // no-op: 原实现会去调 SteamMatchmaking.SetLobbyType(Id, ...) 并在 Steam 未初始化时抛异常。
    // **故意不解引用 self** —— 什么也不做, 也就不需要它有效。
    if (g_lsp_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.SetPublic() 首次被拦截 -> no-op(不再抛 NullReferenceException)");
}

void Hook_LobbySetJoinable(void* /*self*/, bool joinable)
{
    // no-op: 同理不解引用 self。joinable 是基元类型(bool 走第二个寄存器参数的低字节),
    // 记它是安全的 —— 但**不碰 self**。
    if (g_lsj_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.SetJoinable(" + std::string(joinable ? "true" : "false") +
                 ") 首次被拦截 -> no-op(不再抛 NullReferenceException)");
}

void* Hook_LobbyGetId(void* /*self*/)
{
    // 恒返回 0 = SteamId 全零 -> 游戏侧 SteamId.op_Implicit(...) 得 0
    // -> 协议里"无 Steam 大厅"的合法值。
    if (g_lid_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.get_Id 首次被拦截 -> 恒返回 0(SteamId 零值, 游戏侧 steamLobbyId=0)");
    return nullptr;
}

// Steamworks.NET 里 Steamworks.Data.Lobby 的命名空间是 "Steamworks.Data"; 其余为兜底。
const char* kLobbyNamespaces[] = { "Steamworks.Data", "Steamworks", "", "Steamworks.NET" };

// 在域内全部镜像里找 Steamworks.Data.Lobby(与 resolve_steam_matchmaking 同一套路)。
void* resolve_lobby_class(const cesium_safe::il2cpp_tbl& t, std::string* out_image, std::string* out_ns)
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
        log_line("[steamhack] 方案B: IL2CPP 函数表缺失(" + missing + ") —— 无法解析 Steamworks.Data.Lobby");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] 方案B: il2cpp domain 为空, 无法解析 Lobby");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] 方案B: domain_get_assemblies 返回空");
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

        for (const char* ns : kLobbyNamespaces)
        {
            void* cls = t.class_from_name(image, ns, "Lobby");
            if (!cls) continue;
            log_line("[steamhack] 方案B: 命中类 Lobby: image=" + iname + " ns=\"" + std::string(ns) +
                     "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " 类名=" + class_name_of(t, cls));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line("[steamhack] 方案B: 未找到类 Steamworks.Data.Lobby(候选 ns: \"Steamworks.Data\" / "
                 "\"Steamworks\" / 全局 / \"Steamworks.NET\")");
        log_line("[steamhack] 方案B: 域内程序集 " + std::to_string(count) + " 个: " + all);
    }
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

// 诊断(需求): 把 Lobby 类的**字段名列表**(上限 24 条)与**含 Id/Public/Join 的方法名列表**
// (上限 24 条)打进日志 —— 这是判定 "Lobby.Id 到底是字段还是属性" 的旁证。
void dump_lobby_members(const cesium_safe::il2cpp_tbl& t, void* cls)
{
    if (!cls) return;

    // ---- 字段名列表 ----
    if (t.class_get_fields && t.field_get_name)
    {
        void* iter = nullptr;
        std::string names;
        int shown = 0;
        for (;;)
        {
            void* f = t.class_get_fields(cls, &iter);
            if (!f) break;
            const char* n = t.field_get_name(f);
            if (!n) continue;
            if (shown >= 24) { names += ", ..."; break; }
            if (shown) names += ", ";
            names += n;
            shown++;
        }
        log_line("[steamhack] Lobby 类字段名(上限24): " +
                 (names.empty() ? std::string("(无)") : names));
    }
    else
    {
        log_line("[steamhack] Lobby 类字段名: 未能列出"
                 "(缺 il2cpp_class_get_fields 或 il2cpp_field_get_name 导出)");
    }

    // ---- 含 Id/Public/Join 的方法名列表 ----
    if (t.class_get_methods && t.method_get_name)
    {
        void* iter = nullptr;
        std::string names;
        int shown = 0;
        for (;;)
        {
            void* m = t.class_get_methods(cls, &iter);
            if (!m) break;
            const char* n = t.method_get_name(m);
            if (!n) continue;
            if (strstr(n, "Id") || strstr(n, "Public") || strstr(n, "Join"))
            {
                if (shown >= 24) { names += ", ..."; break; }
                if (shown) names += ", ";
                names += n;
                shown++;
            }
        }
        log_line("[steamhack] Lobby 类含 Id/Public/Join 的方法名(上限24): " +
                 (names.empty() ? std::string("(无)") : names));
    }
    else
    {
        log_line("[steamhack] Lobby 类含 Id/Public/Join 的方法名: 未能列出"
                 "(缺 il2cpp_class_get_methods 或 il2cpp_method_get_name 导出)");
    }
}

// 解析并挂钩 Lobby 上的一个"无害化"方法(SetPublic / SetJoinable)。
bool install_lobby_noop(const cesium_safe::il2cpp_tbl& t, void* cls,
                        const char* method_name, int arg_count,
                        void* detour, LPVOID* real_out, void** target_out)
{
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] 方案B: il2cpp_class_get_method_from_name 导出缺失, 无法解析 Lobby." +
                 std::string(method_name));
        return false;
    }

    void* mi = t.class_get_method_from_name(cls, method_name, arg_count);
    if (!mi)
    {
        log_line("[steamhack] 方案B: 未命中 Lobby." + std::string(method_name) + "(" +
                 std::to_string(arg_count) + " 参数) —— 跳过该 hook");
        return false;
    }

    void* target = method_pointer_of(mi);
    log_line("[steamhack] 方案B: 命中 Lobby." + std::string(method_name) + " MethodInfo=" +
             cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)target) +
             " (" + cesium::steam::diagnostics::gameassembly_rva(target) + ")");
    if (!target)
    {
        log_line("[steamhack] 方案B: Lobby." + std::string(method_name) +
                 " 的 methodPointer 为空(可能未 AOT 编译) —— **只记日志, 不挂钩**(fail-safe)");
        return false;
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, detour, real_out);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_CreateHook(Lobby." + std::string(method_name) + ") 失败: " +
                 std::to_string((int)st) + " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") —— 不挂钩");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_EnableHook(Lobby." + std::string(method_name) + ") 失败: " +
                 std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    if (target_out) *target_out = target;
    log_line("[steamhack] 方案B: Lobby." + std::string(method_name) +
             " inline hook 已启用 (no-op, 不再抛 NullReferenceException)");
    return true;
}

// Lobby.Id: **先判定它是字段还是属性**(需求明确要求把结论写进日志), 再决定怎么处理。
//   *out_is_property 回传"存在 get_Id 属性"这一事实(不管挂钩成不成功), 供调用方算预期 hook 数。
//   字段     -> 不可 hook: **只记日志**, 不硬来。
//   属性     -> get_Id() 恒返回 0(SteamId 在 x64 下走 RAX 返回)。
bool install_lobby_id(const cesium_safe::il2cpp_tbl& t, void* cls, bool* out_is_property)
{
    if (out_is_property) *out_is_property = false;

    // ---- 需求: 判定 Id 是字段还是属性(两边的 API 都试, 结论写进日志) ----
    void* fld = (t.class_get_field_from_name ? t.class_get_field_from_name(cls, "Id") : nullptr);
    void* mi = (t.class_get_method_from_name ? t.class_get_method_from_name(cls, "get_Id", 0) : nullptr);

    log_line("[steamhack] Lobby.Id 判定: il2cpp_class_get_field_from_name(\"Id\")=" +
             std::string(fld ? "命中 -> **是字段**" : "未命中 -> 不是字段") +
             " | il2cpp_class_get_method_from_name(\"get_Id\", 0)=" +
             std::string(mi ? "命中 -> **是属性**" : "未命中 -> 没有 get_Id 属性"));

    // 交叉印证: 走属性 API 再确认一次(缺导出就跳过, 只记日志)
    if (t.class_get_property_from_name && t.property_get_get_method)
    {
        void* prop = t.class_get_property_from_name(cls, "Id");
        void* gm = prop ? t.property_get_get_method(prop) : nullptr;
        log_line("[steamhack] Lobby.Id 交叉印证: il2cpp_class_get_property_from_name(\"Id\")=" +
                 std::string(prop ? "命中" : "未命中") + " getter=" + cesium::steam::diagnostics::hex((uintptr_t)gm) +
                 (gm == mi ? " (与 get_Id 一致)" : ""));
    }
    else
    {
        log_line("[steamhack] Lobby.Id 交叉印证: 跳过"
                 "(缺 il2cpp_class_get_property_from_name 或 il2cpp_property_get_get_method 导出)");
    }

    if (fld)
    {
        log_line("[steamhack] Lobby.Id 结论 = **字段**(不是属性) —— 字段不可 hook"
                 "(HybridCLR 解释执行时按字段偏移直读, 没有 AOT 入口可挂)。"
                 "方案B 对 Id **只记日志, 不硬来**; steamLobbyId 的值由方案C(方案C 让 hasValue 为 false "
                 "时根本不会走到 Id)与 hasValue 判定共同决定");
        return false;
    }
    if (!mi)
    {
        log_line("[steamhack] Lobby.Id 结论 = 既不是字段, 也没有 get_Id() 属性 —— 不挂钩(fail-safe)");
        return false;
    }

    if (out_is_property) *out_is_property = true;

    void* target = method_pointer_of(mi);
    if (!target)
    {
        log_line("[steamhack] Lobby.get_Id 的 methodPointer 为空(可能未 AOT 编译) —— "
                 "**只记日志, 不挂钩**(fail-safe)");
        return false;
    }

    // ---- 返回值核对(关键安全门): 只有"<= 8 字节的值类型"才能用 RAX=0 直接返回。
    //      >8 字节的结构体在 x64 ABI 下走**隐藏返回缓冲区**(调用方传缓冲区指针), 那时返回 0
    //      会写错位置 —— 那才是真的会崩。有 il2cpp_class_value_size 就核对, 没有就只记日志
    //      (预期是 Steamworks.Data.SteamId, 8 字节)。
    if (t.method_get_return_type && t.type_get_name)
    {
        void* rt = t.method_get_return_type(mi);
        const std::string rt_name = cstr_or(t.type_get_name(rt), "(null)");
        void* rt_obj = (rt && t.type_get_object) ? t.type_get_object(rt) : nullptr;
        void* rt_cls = (rt_obj && t.class_from_system_type) ? t.class_from_system_type(rt_obj) : nullptr;

        bool have_vt = false, is_vt = false;
        if (rt_cls && t.class_is_valuetype)
        {
            is_vt = t.class_is_valuetype(rt_cls);
            have_vt = true;
        }
        bool have_sz = false;
        unsigned sz = 0;
        if (rt_cls && t.class_value_size)
        {
            sz = (unsigned)t.class_value_size(rt_cls, nullptr);
            have_sz = true;
        }

        log_line("[steamhack] Lobby.get_Id 返回类型=" + rt_name +
                 " 值类型=" + (have_vt ? (is_vt ? "是" : "否") : "未能判定") +
                 (have_sz ? (" 尺寸=" + std::to_string(sz) + " 字节")
                          : std::string(" 尺寸=(il2cpp_class_value_size 导出缺失, 未核对)")));

        if (have_vt && is_vt && have_sz && sz > 8)
        {
            log_line("[steamhack] Lobby.get_Id 返回结构体 " + std::to_string(sz) +
                     " 字节(>8) —— x64 ABI 下走**隐藏返回缓冲区**, 用 RAX=0 返回会写错位置, "
                     "**不挂钩**(fail-safe; 由方案C 让 hasValue=false 来避免走到这里)");
            return false;
        }
        if (have_vt && !is_vt)
        {
            log_line("[steamhack] Lobby.get_Id 返回的是**引用类型**(" + rt_name +
                     ") —— 返回 0 会变成 null 引用(语义不对, 可能引出别的异常), **不挂钩**(fail-safe)");
            return false;
        }
        if (!have_sz)
            log_line("[steamhack] Lobby.get_Id: 未能核对返回结构体尺寸 —— 按"
                     "“SteamId 是 8 字节值类型、走 RAX 返回”的预期继续(这是方案B 唯一的 ABI 假设)");
    }
    else
    {
        log_line("[steamhack] Lobby.get_Id: 无法核对返回类型(缺 il2cpp_method_get_return_type / "
                 "il2cpp_type_get_name) —— 按“SteamId 是 8 字节值类型、走 RAX 返回”的预期继续");
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_LobbyGetId, (LPVOID*)&g_real_lid);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_CreateHook(Lobby.get_Id) 失败: " + std::to_string((int)st) +
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") —— 不挂钩");
        return false;
    }
    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_EnableHook(Lobby.get_Id) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_lid_target = target;
    g_lid_method = mi;
    g_lid_hooked = true;
    log_line("[steamhack] 方案B: Lobby.get_Id inline hook 已启用 (恒返回 SteamId 零值 -> steamLobbyId=0)");
    return true;
}

// 安装方案B 的全部 hook。返回实际装上的数量(0~3);
// *out_expected 回传预期数量(Id 是**属性**时 3, 是字段/不存在时 2)。
int install_lobby_methods(const cesium_safe::il2cpp_tbl& t, int* out_expected)
{
    int installed = 0;
    if (out_expected) *out_expected = 2;   // SetPublic + SetJoinable

    std::string image;
    std::string ns;
    void* cls = resolve_lobby_class(t, &image, &ns);
    if (!cls)
    {
        log_line("[steamhack] 方案B: 未能解析 Steamworks.Data.Lobby 类 —— 跳过 Lobby 方法无害化"
                 "(fail-safe, 行为保持当前状态)");
        return 0;
    }

    const bool cls_is_vt = (t.class_is_valuetype ? t.class_is_valuetype(cls) : false);
    log_line("[steamhack] 方案B: 采用类 Lobby image=" + image + " ns=\"" + ns +
             "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) + " 类名=" + class_name_of(t, cls) +
             " 值类型=" + (cls_is_vt ? "是(struct)" : "否(class)"));

    // 需求: 字段名列表 + 含 Id/Public/Join 的方法名列表, 各上限 24 条
    dump_lobby_members(t, cls);

    if (install_lobby_noop(t, cls, "SetPublic", 0, (void*)&Hook_LobbySetPublic,
                           (LPVOID*)&g_real_lsp, &g_lsp_target))
    {
        g_lsp_hooked = true;
        installed++;
    }
    if (install_lobby_noop(t, cls, "SetJoinable", 1, (void*)&Hook_LobbySetJoinable,
                           (LPVOID*)&g_real_lsj, &g_lsj_target))
    {
        g_lsj_hooked = true;
        installed++;
    }

    bool id_is_property = false;
    if (install_lobby_id(t, cls, &id_is_property)) installed++;
    if (out_expected) *out_expected = 2 + (id_is_property ? 1 : 0);

    return installed;
}

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
        log_line("[steamhack] LobbyQuery.RequestAsync 首次被拦截: this=" +
                 cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(this_ptr)) +
                 " (LobbyQuery 按引用传参, 本模块不解引用) 第2寄存器=" +
                 cesium::steam::diagnostics::hex(reinterpret_cast<uintptr_t>(method_info)) +
                 " -> 恒返回预建好的 Task<Lobby[]>(结果 = 长度 0 的 Lobby[]); "
                 "游戏侧 array != null && array.Length > 0 为 false -> 跳过 Steam 大厅清理, "
                 "await 之后的 UpdateRoomByExit / ClearRoomInfo 正常执行");
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
        log_line(std::string("[steamhack] ") + label + ": IL2CPP 函数表缺失(" + missing +
                 ") —— 无法解析 " + class_name + ", 不挂钩(fail-safe)");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line(std::string("[steamhack] ") + label + ": il2cpp domain 为空 —— 解析不到 " + class_name);
        return nullptr;
    }
    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line(std::string("[steamhack] ") + label + ": domain_get_assemblies 返回空");
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
            log_line(std::string("[steamhack] ") + label + ": 命中类 " + class_name + ": image=" + iname +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " 类名=" + class_name_of(t, cls) +
                     " 值类型=" + (is_vt ? "是" : "否"));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line(std::string("[steamhack] ") + label + ": 未找到类 " + class_name +
                 "(候选 ns: \"Steamworks.Data\" / \"Steamworks\" / 全局 / \"Steamworks.NET\")");
        log_line(std::string("[steamhack] ") + label + ": 域内程序集 " + std::to_string(count) + " 个: " + all);
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

// 探针: 校验预建好的 Task 是 "IsCompleted == true 且 get_Result() 是**长度 0** 的 Lobby[]"。
// 返回 true = 校验通过(可以挂钩); false = 不可信(**不挂钩**, 宁可不挂钩也不给游戏一个错误对象)。
// 复用方案C 的 pod_runtime_invoke(已带 __try 兜底)与 boxed_byte。
bool probe_lobby_query_task(const cesium_safe::il2cpp_tbl& t, void* task, void* task_cls,
                            void* array_cls, int* out_len, int* out_len_src)
{
    if (out_len) *out_len = -1;
    if (out_len_src) *out_len_src = 0;

    if (!task || !t.runtime_invoke)
    {
        log_line("[steamhack] 阶段5[探针]: 缺 il2cpp_runtime_invoke 或任务对象为空 —— "
                 "跳过完成态/结果校验(仅按构造成功放行)");
        return true;
    }

    void* cls = t.object_get_class ? t.object_get_class(task) : nullptr;
    if (!cls) cls = task_cls;
    if (!cls)
    {
        log_line("[steamhack] 阶段5[探针]: 判不出任务对象的类 —— 跳过校验");
        return true;
    }

    void* args[1] = { nullptr };

    // ---- 1) 完成态: IsCompleted 必须为 true(否则游戏 await 会永久挂起) ----
    void* get_completed = t.class_get_method_from_name
                              ? t.class_get_method_from_name(cls, "get_IsCompleted", 0)
                              : nullptr;
    if (!get_completed) get_completed = find_method_up_chain(t, cls, "get_IsCompleted");
    if (!get_completed || !method_pointer_of(get_completed))
    {
        log_line("[steamhack] 阶段5[探针]: 找不到可用的 IsCompleted getter —— 跳过完成态校验");
    }
    else
    {
        void* exc = nullptr;
        void* boxed = nullptr;
        unsigned long seh = 0;
        pod_runtime_invoke((void*)t.runtime_invoke, get_completed, task, args, &boxed, &exc, &seh);
        if (seh || exc || !boxed)
        {
            log_line(std::string("[steamhack] 阶段5[探针]: IsCompleted 调用失败") +
                     (seh ? ("(SEH " + hex_code_str(seh) + ")") : exc ? "(托管异常)" : "(返回空)") +
                     " —— 校验失败, 不挂钩");
            return false;
        }
        const bool completed = boxed_byte(t, boxed) != 0;
        log_line("[steamhack] 阶段5[探针]: IsCompleted=" + std::string(completed ? "true" : "false"));
        if (!completed)
        {
            log_line("[steamhack] 阶段5[探针]: 任务不是 RanToCompletion —— 不挂钩"
                     "(否则游戏 await 会永久挂起)");
            return false;
        }
    }

    // ---- 2) get_Result() 必须是长度 0 的 Lobby[] ----
    // 已完成 -> get_Result 不会阻塞, 可以安全调用。
    void* get_result = t.class_get_method_from_name
                           ? t.class_get_method_from_name(cls, "get_Result", 0)
                           : nullptr;
    if (!get_result || !method_pointer_of(get_result))
    {
        log_line("[steamhack] 阶段5[探针]: 任务类上拿不到 get_Result() —— 无法校验结果, 判失败, 不挂钩");
        return false;
    }
    void* rexc = nullptr;
    void* rv = nullptr;
    unsigned long rseh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_result, task, args, &rv, &rexc, &rseh);
    if (rseh || rexc)
    {
        log_line(std::string("[steamhack] 阶段5[探针]: get_Result() 调用失败") +
                 (rseh ? ("(SEH " + hex_code_str(rseh) + ")") : "(托管异常)") + " —— 不挂钩");
        return false;
    }
    if (!rv)
    {
        // 语义上 null 也能让游戏跳过(array != null 为 false), 但我们的目标是"长度 0 的数组";
        // 拿到 null 说明 ctor 没把结果存进去(或实参被降级成 nullptr) —— 按"宁可不挂钩"处理。
        log_line("[steamhack] 阶段5[探针]: get_Result() 返回 null(不是长度 0 的 Lobby[]) —— "
                 "校验失败, 不挂钩(说明 ctor 没把数组存进结果)");
        return false;
    }

    // 结果对象的类是否是 Lobby[](只在两边都解析得出时判定)
    bool class_checked = false;
    bool class_is_array = false;
    if (t.object_get_class)
    {
        void* rcls = t.object_get_class(rv);
        class_checked = true;
        class_is_array = (rcls != nullptr && array_cls != nullptr && rcls == array_cls);
        log_line("[steamhack] 阶段5[探针]: get_Result() 非空 对象=" + cesium::steam::diagnostics::hex((uintptr_t)rv) +
                 " 类=" + class_name_of(t, rcls) + " 与 Lobby[] 的 Il2CppClass 一致=" +
                 (array_cls ? (class_is_array ? "是" : "否") : "(未解析到 Lobby[], 跳过该判定)"));
    }

    // ---- 3) 元素个数必须是 0 ----
    PodArrayLength al;
    pod_array_length(&t, rv, &al);
    if (al.seh_code)
    {
        log_line("[steamhack] 阶段5[探针]: 读数组长度触发 SEH " + hex_code_str(al.seh_code) +
                 " —— 已被 __try/__except 捕获; 未能读取长度, 只按'非 NULL'放行");
    }
    else if (al.length >= 0)
    {
        log_line("[steamhack] 阶段5[探针]: 结果数组长度=" + std::to_string(al.length) + " (来源: " +
                 (al.source == 1 ? "il2cpp_array_length(权威导出)"
                                 : "数组布局偏移(il2cpp_array_object_header_size - 8)") + ")");
    }
    else
    {
        log_line("[steamhack] 阶段5[探针]: 未能读取数组长度(il2cpp_array_length 与 "
                 "il2cpp_array_object_header_size 都不可用) —— 只校验'非 NULL'");
    }
    if (out_len) *out_len = al.length;
    if (out_len_src) *out_len_src = al.source;

    if (al.length >= 0 && al.length != 0)
    {
        log_line("[steamhack] 阶段5[探针]: 结果数组长度不为 0 —— 校验失败, 不挂钩"
                 "(否则游戏会拿这个数组去 Leave() 真实大厅)");
        return false;
    }
    if (class_checked && array_cls && !class_is_array)
    {
        log_line("[steamhack] 阶段5[探针]: 结果对象的类不是 Lobby[] —— 校验失败, 不挂钩");
        return false;
    }

    log_line("[steamhack] 阶段5[探针]: 校验通过 —— 已完成 + get_Result() 是长度 0 的 Lobby[]"
             "(游戏侧 array != null && array.Length > 0 为 false, 跳过 Steam 大厅清理)");
    return true;
}

// 在 cls 上按**参数类型**精确挑出 ".ctor(1)"。
//
// 为什么不能只用 class_get_method_from_name(cls, ".ctor", 1):
//   System.Threading.Tasks.Task`1 上存在**多个** 1 参数构造函数 ——
//       internal Task(TResult result)              <- 我们要的这个(造出来就是 RanToCompletion)
//       public   Task(Func<TResult> function)      <- 名字/参数个数都匹配, 但不是我们要的
//       public   Task(Func<object, TResult> function) 等
//   class_get_method_from_name 返回的是"第一个 名字+参数个数 匹配"的那个, 未必是 Task(TResult)。
//   方案C(阶段2/4)实测拿到的确实是对的(探针 IsCompleted=true 通过), 但那是元数据顺序的运气;
//   这里**显式**按"参数类型 == Lobby[] 的 Il2CppClass"筛一遍。
// 筛不出来绝不硬来: 返回 nullptr 由调用方退回 class_get_method_from_name, 那条结果**仍要过探针校验**
//   —— 拿错 ctor 时造出来的任务 IsCompleted=false, 探针会拦下 -> 不挂钩(而不是给游戏一个坏对象)。
void* find_task_ctor_with_arg(const cesium_safe::il2cpp_tbl& t, void* cls, void* expected_arg_cls,
                              std::string* out_param_name, int* out_candidates)
{
    if (out_candidates) *out_candidates = 0;
    if (out_param_name) *out_param_name = "(未知)";
    if (!cls || !t.class_get_methods || !t.method_get_name || !t.method_get_param_count ||
        !t.method_get_param || !t.type_get_object || !t.class_from_system_type)
        return nullptr;

    void* found = nullptr;
    void* iter = nullptr;
    for (;;)
    {
        void* m = t.class_get_methods(cls, &iter);
        if (!m) break;
        const char* n = t.method_get_name(m);
        if (!n || strcmp(n, ".ctor") != 0) continue;
        if (t.method_get_param_count(m) != 1) continue;
        if (out_candidates) (*out_candidates)++;

        void* pt = t.method_get_param(m, 0);
        if (!pt) continue;
        void* pto = t.type_get_object(pt);
        void* pcls = pto ? t.class_from_system_type(pto) : nullptr;
        const std::string pn = t.type_get_name ? cstr_or(t.type_get_name(pt), "(null)")
                                               : std::string("(null)");

        if (expected_arg_cls && pcls == expected_arg_cls)
        {
            found = m;
            if (out_param_name) *out_param_name = pn;
            log_line("[steamhack] 阶段5: **已按参数类型精确命中** 类..ctor(1): 参数类型全名=" + pn +
                     " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) + " (与 Lobby[] 一致) MethodInfo=" +
                     cesium::steam::diagnostics::hex((uintptr_t)m));
            break;
        }
        log_line("[steamhack] 阶段5: 枚举到一个 1 参数 .ctor 但**参数类型不匹配** —— 参数类型全名=" + pn +
                 " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) + " (期望 Lobby[]=" +
                 cesium::steam::diagnostics::hex((uintptr_t)expected_arg_cls) + ")");
    }
    return found;
}

// 安装阶段5 hook。返回 true = 已挂上。
// 顺序严格遵循"**先把返回值造出来并校验通过, 再挂钩**":
//   解析 LobbyQuery / RequestAsync / Task<Lobby[]> / Lobby / .ctor(1) -> 造长度 0 的 Lobby[]
//   -> direct 直调 ctor 造 Task -> 探针校验 -> 记 GC root -> MH_CreateHook。
// 任何一步失败都**只记日志、不挂钩**(fail-safe)。
bool install_lobby_query_hook(const cesium_safe::il2cpp_tbl& t)
{
    log_line("[steamhack] 阶段5: 安装 LobbyQuery.RequestAsync 兜底"
             "(退房/解散/被踢时 await 之后的代码不再被 NRE 跳过) ...");

    // ---- 依赖导出: 缺任何一个都做不了, 直接放弃该 hook(游戏行为与原版一致) ----
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",                 (const void*)t.domain_get },
        { "domain_get_assemblies",      (const void*)t.domain_get_assemblies },
        { "assembly_get_image",         (const void*)t.assembly_get_image },
        { "image_get_name",             (const void*)t.image_get_name },
        { "class_from_name",            (const void*)t.class_from_name },
        { "class_get_method_from_name", (const void*)t.class_get_method_from_name },
        { "method_get_return_type",     (const void*)t.method_get_return_type },
        { "type_get_object",            (const void*)t.type_get_object },
        { "class_from_system_type",     (const void*)t.class_from_system_type },
        { "object_new",                 (const void*)t.object_new },
        { "array_new",                  (const void*)t.array_new },
        { "runtime_invoke",             (const void*)t.runtime_invoke },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line("[steamhack] 阶段5: IL2CPP 导出缺失(" + missing + ") —— 不挂钩(fail-safe)");
        return false;
    }

    // ---- 1) 解析 Steamworks.Data.LobbyQuery ----
    std::string lq_image;
    std::string lq_ns;
    void* lq_cls = resolve_steamworks_class(t, "阶段5", "LobbyQuery", &lq_image, &lq_ns);
    if (!lq_cls) return false;
    const bool lq_is_vt = t.class_is_valuetype ? t.class_is_valuetype(lq_cls) : false;
    log_line("[steamhack] 阶段5: LobbyQuery 采用 image=" + lq_image + " ns=\"" + lq_ns + "\" 值类型=" +
             (lq_is_vt ? "是(struct —— 与反编译里 `(ref lobbyList)` 的写法一致)"
                       : "否(class —— 反编译里的 `(ref ...)` 也可作用于类, 不影响本 hook)"));

    // ---- 2) 解析 RequestAsync(0 参数)并取 methodPointer ----
    void* mi = t.class_get_method_from_name(lq_cls, "RequestAsync", 0);
    if (!mi)
    {
        log_line("[steamhack] 阶段5: 未命中 LobbyQuery.RequestAsync(0 参数) —— 不挂钩(fail-safe)");
        dump_class_methods(t, lq_cls, "阶段5.LobbyQuery");
        return false;
    }
    void* target = method_pointer_of(mi);
    const uint32_t pc = t.method_get_param_count ? t.method_get_param_count(mi) : 0;
    log_line("[steamhack] 阶段5: 命中 LobbyQuery.RequestAsync: image=" + lq_image + " ns=\"" + lq_ns +
             "\" MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)target) +
             " (" + cesium::steam::diagnostics::gameassembly_rva(target) + ") 参数个数=" + std::to_string(pc));
    if (!target)
    {
        log_line("[steamhack] 阶段5: RequestAsync 的 methodPointer 为空(可能不是 AOT 方法) —— "
                 "**只记日志, 不挂钩**(fail-safe)");
        return false;
    }

    // ---- 3) 返回类型 -> Task<Lobby[]> 的 Il2CppClass(需求: 日志打印解析到的返回类型全名) ----
    void* ret = t.method_get_return_type(mi);
    if (!ret)
    {
        log_line("[steamhack] 阶段5: method_get_return_type 返回空 —— 不挂钩(fail-safe)");
        return false;
    }
    const std::string ret_name =
        t.type_get_name ? cstr_or(t.type_get_name(ret), "(null)") : std::string("(type_get_name 缺失)");
    void* tobj = t.type_get_object(ret);
    void* task_cls = tobj ? t.class_from_system_type(tobj) : nullptr;
    log_line("[steamhack] 阶段5: 返回值解析 **返回类型全名 = " + ret_name + "**" +
             " Il2CppType=" + cesium::steam::diagnostics::hex((uintptr_t)ret) + " System.Type=" + cesium::steam::diagnostics::hex((uintptr_t)tobj) +
             " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)task_cls) + " 类名=" + class_name_of(t, task_cls));
    if (!task_cls)
    {
        log_line("[steamhack] 阶段5: class_from_system_type(" + ret_name + ") 返回空 —— 不挂钩(fail-safe)");
        return false;
    }
    {
        // 期望形如 System.Threading.Tasks.Task`1<Steamworks.Data.Lobby[]>。
        // 只做"看起来对"的提示(不作硬门槛 —— 硬门槛会因命名拼写差异误杀), 不符时明确写在日志里。
        const bool looks_task = (ret_name.find("Task") != std::string::npos);
        const bool looks_lobby = (ret_name.find("Lobby") != std::string::npos);
        log_line("[steamhack] 阶段5: 返回类型自检 含\"Task\"=" + std::string(looks_task ? "是" : "否") +
                 " 含\"Lobby\"" + "=" + (looks_lobby ? "是" : "否") +
                 " (期望 System.Threading.Tasks.Task`1<Steamworks.Data.Lobby[]>)" +
                 (looks_task && looks_lobby ? " —— 与预期一致"
                                            : " —— **与预期不符, 仍按实际类型继续, 但请核对上面那行**"));
    }

    // ---- 4) 解析 Steamworks.Data.Lobby(空数组的元素类型) ----
    std::string lobby_image;
    std::string lobby_ns;
    void* lobby_cls = resolve_steamworks_class(t, "阶段5", "Lobby", &lobby_image, &lobby_ns);
    void* array_cls = nullptr;
    if (lobby_cls && t.array_class_get)
    {
        array_cls = t.array_class_get(lobby_cls, 1);
        log_line("[steamhack] 阶段5: Lobby 采用 image=" + lobby_image + " ns=\"" + lobby_ns +
                 "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)lobby_cls) +
                 " -> Lobby[] Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)array_cls));
    }
    if (!lobby_cls)
    {
        log_line("[steamhack] 阶段5: 未解析到 Steamworks.Data.Lobby —— 无法构造长度 0 的数组, "
                 "不挂钩(fail-safe; 不用 nullptr 硬来, 因为那会让探针判不出结果类型)");
        return false;
    }

    // ---- 5) Task<Lobby[]>::.ctor(1) + 参数类型判定 ----
    // 先按参数类型精确筛(Task<T> 上有多个 1 参数 ctor), 筛不到再退回名字+个数匹配。
    int ctor_candidates = 0;
    std::string ctor_pick_name;
    void* ctor = nullptr;
    if (array_cls)
    {
        ctor = find_task_ctor_with_arg(t, task_cls, array_cls, &ctor_pick_name, &ctor_candidates);
        log_line("[steamhack] 阶段5: 类上共枚举到 " + std::to_string(ctor_candidates) +
                 " 个 1 参数 .ctor(按参数类型筛选后命中=" + std::string(ctor ? "是" : "否") + ")");
    }
    if (!ctor)
    {
        ctor = t.class_get_method_from_name(task_cls, ".ctor", 1);
        log_line("[steamhack] 阶段5: 未按参数类型精确筛出 .ctor(1)(Lobby[]) —— 退回 "
                 "class_get_method_from_name(类, \".ctor\", 1); 该结果**仍要过探针校验**, "
                 "若拿到的不是 Task<TResult>(TResult), 会因 IsCompleted=false 被拦下(fail-safe)");
    }
    void* ctor_ptr = method_pointer_of(ctor);
    log_line("[steamhack] 阶段5: 采用的 类..ctor(1) 命中=" + std::string(ctor ? "是" : "否") +
             " MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)ctor) +
             " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)ctor_ptr));
    if (!ctor || !ctor_ptr)
    {
        log_line("[steamhack] 阶段5: 拿不到 类..ctor(1) 的 methodPointer —— 不挂钩(fail-safe)");
        return false;
    }

    bool param_is_vt = false;
    bool param_determined = false;
    std::string param_name = "(未知)";
    if (t.method_get_param_count && t.method_get_param)
    {
        const uint32_t cpc = t.method_get_param_count(ctor);
        log_line("[steamhack] 阶段5: ctor 参数个数=" + std::to_string(cpc));
        if (cpc == 1)
        {
            void* pt = t.method_get_param(ctor, 0);
            if (pt && t.type_get_name) param_name = cstr_or(t.type_get_name(pt), "(null)");
            if (pt && t.type_get_object && t.class_from_system_type && t.class_is_valuetype)
            {
                void* pto = t.type_get_object(pt);
                void* pcls = pto ? t.class_from_system_type(pto) : nullptr;
                if (pcls)
                {
                    param_is_vt = t.class_is_valuetype(pcls);
                    param_determined = true;
                    if (array_cls)
                        log_line("[steamhack] 阶段5: ctor 参数类 Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) +
                                 " 与解析到的 Lobby[] 一致=" + ((pcls == array_cls) ? "是" : "否"));
                }
            }
        }
    }
    log_line("[steamhack] 阶段5: ctor 参数类型全名=" + param_name + " 值类型=" +
             (param_determined ? (param_is_vt ? "**是**(不符合预期)" : "否(引用类型, 符合预期)")
                               : "未知(无法判定, 不冒进)"));
    if (param_determined && param_is_vt)
    {
        // 需求: 参数被判定为值类型就**不挂钩** —— 给 Task<Lobby[]> 的 ctor 传"数组指针"是错的,
        // 用零值装箱更会给出一个错误的数组。宁可不挂钩, 也不要给游戏一个错误对象。
        log_line("[steamhack] 阶段5: ctor 参数被判定为**值类型**(不符合预期 —— 泛型参数应为引用类型 "
                 "Lobby[]) —— 不用零值装箱(那会给出错误的数组), **不挂钩**(fail-safe)");
        return false;
    }

    // ---- 6) 造长度 0 的 Lobby[] ----
    // il2cpp_array_new 的第 1 个参数是**元素类型**(Lobby), 不是数组类(那是 il2cpp_array_new_specific),
    // 所以传 lobby_cls, 得到的才是 Lobby[](传 array_cls 会得到 Lobby[][])。
    void* empty_array = nullptr;
    unsigned long an_seh = 0;
    pod_array_new_empty((void*)t.array_new, lobby_cls, &empty_array, &an_seh);
    if (an_seh)
        log_line("[steamhack] 阶段5: il2cpp_array_new(Lobby, 0) 触发 SEH " + hex_code_str(an_seh) +
                 " —— 已被 __try/__except 捕获");

    if (empty_array)
    {
        PodArrayLength al0;
        pod_array_length(&t, empty_array, &al0);
        log_line("[steamhack] 阶段5: 已构造空数组 Lobby[0] 对象=" + cesium::steam::diagnostics::hex((uintptr_t)empty_array) +
                 " 元素个数=" + (al0.length >= 0 ? std::to_string(al0.length) : std::string("(读不到)")) +
                 " (来源: " + (al0.source == 1 ? "il2cpp_array_length"
                                               : al0.source == 2 ? "数组布局偏移" : "无") + ")");
    }
    else
    {
        // 降级: 传 nullptr。调用方有 `array != null` 判空, 语义安全 —— 但探针会因此拿到 null 结果
        // 而判定失败(见 probe_lobby_query_task), 所以最终**不会挂钩**; 这里明确记下走到了哪一步。
        log_line("[steamhack] 阶段5: **降级路径** —— 未能构造长度 0 的 Lobby[](il2cpp_array_new 调用"
                 "失败/返回空)。按需求本可退化为给 ctor 传 nullptr(调用方有 `array != null` 判空, "
                 "语义安全), 但探针拿不到数组就无法校验结果类型, 因此预期会走到'探针失败 -> 不挂钩'。");
    }

    // ---- 7) direct 直调 ctor: 原生 ABI, 绕过 il2cpp_runtime_invoke ----
    PodLobbyQueryCtor dr;
    pod_call_lobby_query_ctor((void*)t.object_new, task_cls, ctor_ptr, ctor, empty_array, &dr);
    {
        std::string s = "[steamhack] 阶段5[direct]: 原生 ABI 直调 Task<Lobby[]>::.ctor(Lobby[]), "
                        "绕过 il2cpp_runtime_invoke: ";
        s += "ctor methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)ctor_ptr);
        s += " 目标类=" + class_name_of(t, task_cls);
        s += " il2cpp_object_new=" + std::string(dr.alloc_ok ? "成功" : "失败");
        s += " 实参=" + (dr.arg_was_null
                             ? std::string("nullptr(降级路径: 没传空数组)")
                             : ("长度 0 的 Lobby[] 对象指针 " + cesium::steam::diagnostics::hex((uintptr_t)empty_array)));
        if (dr.seh_code)
            s += " | 原生调用触发 SEH 异常 " + hex_code_str(dr.seh_code) +
                 " —— 已被 __try/__except 捕获, **构造失败**";
        else if (!dr.called)
            s += " | 未执行调用(缺 object_new / ctor methodPointer / 目标类)";
        else
            s += std::string(" | 原生调用完成, 返回对象=") +
                 (dr.object ? cesium::steam::diagnostics::hex((uintptr_t)dr.object) : std::string("(空)"));
        log_line(s);
    }
    if (!dr.object)
    {
        log_line("[steamhack] 阶段5: 未能构造出 Task<Lobby[]> 对象 —— 不挂钩(fail-safe)");
        return false;
    }

    // ---- 8) 探针校验(安装期, 全程 SEH 兜底) ----
    int arr_len = -1;
    int arr_len_src = 0;
    if (!probe_lobby_query_task(t, dr.object, task_cls, array_cls, &arr_len, &arr_len_src))
    {
        log_line("[steamhack] 阶段5: 探针校验未通过 —— **不挂钩**"
                 "(宁可不挂钩, 也不给游戏一个错误的返回值)");
        return false;
    }

    // ---- 9) 记 GC root + 缓存(所有调用复用同一个对象) ----
    g_lq_task = dr.object;
    g_lq_empty_array = empty_array;
    g_lq_task_class = task_cls;
    g_lq_array_class = array_cls;

    unsigned long gh_seh = 0;
    uint32_t gh = 0;
    if (t.gchandle_new)
    {
        pod_gchandle_new((void*)t.gchandle_new, dr.object, &gh, &gh_seh);
        if (gh_seh)
            log_line("[steamhack] 阶段5[GC root]: il2cpp_gchandle_new 触发 SEH " + hex_code_str(gh_seh) +
                     " —— 已被 __try/__except 捕获, 本次仅靠模块级全局变量作根");
        else
            g_lq_task_gchandle = gh;
    }
    log_line("[steamhack] 阶段5[GC root]: 预建好的 Task 持于模块级全局 g_lq_task=" +
             cesium::steam::diagnostics::hex((uintptr_t)g_lq_task) + " / g_lq_empty_array=" + cesium::steam::diagnostics::hex((uintptr_t)g_lq_empty_array) +
             " (静态存储期, Boehm 会扫描已加载模块的可写数据段)" +
             (g_lq_task_gchandle ? (" + **显式** il2cpp_gchandle_new 句柄=" +
                                    std::to_string(g_lq_task_gchandle) + " (pinned=false)")
                                 : std::string(" ; il2cpp_gchandle_new ") +
                                       (t.gchandle_new ? "返回 0(未成功)" : "导出缺失") +
                                       " —— 只靠全局变量, 如实标注"));
    log_line("[steamhack] 阶段5: 该 Task **只构造一次, 所有 RequestAsync 调用复用同一个实例** "
             "(Task<T> 不可变, get_Result() 恒返回同一个长度 0 的数组)");

    // ---- 10) 挂钩 ----
    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_LobbyQueryRequestAsync, (LPVOID*)&g_real_lq_request);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(LobbyQuery.RequestAsync) 失败: " + std::to_string((int)st) +
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") —— 本次不挂钩(fail-safe)");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(LobbyQuery.RequestAsync) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_lq_target = target;
    g_lq_method = mi;
    g_lq_hooked = true;
    log_line("[steamhack] LobbyQuery.RequestAsync inline hook 已启用 "
             "(恒返回预建好的 Task<Lobby[]>, 结果 = 长度 0 的 Lobby[])");
    return true;
}

// 通用辅助(定义在下面, 两个 hook 处理器都要用)
void* make_default_completed_task(void* hooked_mi);

void* Hook_CreateLobbyAsync(int32_t max_members)
{
    // 只在首次拦截时记录入参(原子计数, 不刷屏)
    if (g_cl_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] CreateLobbyAsync 首次被拦截: maxMembers=" + std::to_string(max_members) +
                 " -> 返回已完成的空 Task(不执行真实 Steam 大厅逻辑, 游戏侧 LobbyId 保持 0)");
    return make_default_completed_task(g_cl_method);
}

void* Hook_JoinLobbyAsync(void* steam_id_ref)
{
    if (g_jl_hits.fetch_add(1, std::memory_order_relaxed) == 0)
    {
        // **故意不解引用**: SteamId 是值类型。IL2CPP 生成的 AOT C++ 通常把值类型参数按"指向该值的
        // 指针"传递, 但本模块无法在运行期确认这一点 —— 万一它其实是按值放在寄存器里, 解引用就是
        // 直接访问违例(崩游戏)。这个参数对本 hook 的逻辑毫无作用(一律返回空 Task), 所以只记原始
        // 寄存器值: 宁可少一条信息, 不可多一个崩溃点。(对比 CreateLobbyAsync 的 int32 参数:
        // 基元类型按值传, 所以那边 maxMembers 的日志是可靠的。)
        const uintptr_t raw = reinterpret_cast<uintptr_t>(steam_id_ref);
        log_line("[steamhack] JoinLobbyAsync 首次被拦截: steamId 原始寄存器值=" +
                 (steam_id_ref ? cesium::steam::diagnostics::hex(raw) : std::string("(null)")) +
                 " (值类型; 按 IL2CPP AOT 约定应是指向 SteamId 的指针, 本模块不解引用)"
                 " -> 返回已完成的空 Task(不执行真实 Steam 大厅逻辑)");
    }
    return make_default_completed_task(g_jl_method);
}

// 传入被挂钩方法的 MethodInfo*(= 任务描述里的 hookedMi), 返回"已完成且结果为 null"的默认返回值。
// 返回值类型由该方法**自己**推导(resolve_task_factory), 所以对 Task<Lobby?> 以外的返回类型
// 也自动适配 —— 不硬编码 Lobby。
// runtime_invoke 失败时记日志并返回 nullptr(不让托管异常穿到游戏里)。
void* make_default_completed_task(void* hooked_mi)
{
    TaskFactory* f = nullptr;
    if (hooked_mi && hooked_mi == g_cl_method) f = &g_factory_cl;
    else if (hooked_mi && hooked_mi == g_jl_method) f = &g_factory_jl;

    if (!f || !g_t_ready)
    {
        // 理论到不了这里(hook 只在安装成功后才存在)。防御性记录一次。
        static std::atomic<long> once{ 0 };
        if (once.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] make_default_completed_task: 未登记的 MethodInfo 或函数表未就绪 -> 返回 nullptr");
        return nullptr;
    }

    if (!f->resolved)   // 防御: 正常情况下安装期已解析 + 探测成功
        resolve_task_factory(g_t, *f, hooked_mi, f->label);

    if (!f->ok || !f->factory)
    {
        if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f->label) +
                     ": 默认返回值工厂不可用 -> 本次返回 nullptr(游戏侧会收到空 Task 而 NRE)");
        return nullptr;
    }

    // ---- 按安装期探测成功的路径造返回值 ----
    // 方案C(direct): 原生 ABI 直调 ctor 的 methodPointer, 绕过 il2cpp_runtime_invoke 的编组。
    // 其余情况(invoke): 仍然走 runtime_invoke(含 FromResult / CompletedTask / 回退)。
    void* exc = nullptr;
    void* r = nullptr;

    if (f->ctor_path == CPATH_DIRECT)
    {
        PodDirectCtor dr;
        r = run_direct_ctor(g_t, *f, &dr);
        // 运行期只记首次详细日志(不刷屏); 失败也只在首次记录, 避免每次点创建房间都刷一条。
        if (f->runtime_logged.fetch_add(1, std::memory_order_relaxed) == 0)
            log_direct_attempt(g_t, *f, dr, r, "运行期首次");
        if (!r)
        {
            if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
                log_line("[steamhack] " + std::string(f->label) +
                         "[direct]: 原生 ABI 直调造默认返回值失败(见上一条) -> 本次返回 nullptr"
                         "(不让异常穿到游戏里)");
            return nullptr;
        }
        return r;
    }

    r = invoke_task_factory(g_t, *f, &exc);
    if (!r)
    {
        if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f->label) + "[invoke]: runtime_invoke 造默认返回值失败" +
                     (exc ? "(托管异常)" : "(返回空)") + " -> 本次返回 nullptr(不让异常穿到游戏里)");
        return nullptr;
    }
    return r;
}

} // namespace

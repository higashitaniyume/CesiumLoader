// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- SteamMatchmaking 解析 + 阶段3 + 方案B 状态 ----------

// Steamworks.NET 的实际命名空间是 "Steamworks"; 后两个是兜底(万一被 flat 到全局或改名)。
const char* kMatchmakingNamespaces[] = { "Steamworks", "", "Steamworks.NET" };

// 在域内全部镜像(此刻 = AOT 镜像)里找 Steamworks.SteamMatchmaking。
// 返回选中的 Il2CppClass*, 实际命中的 image 名 / 命名空间经出参回传(供日志);
// 找不到返回 nullptr(诊断信息, 含域内程序集名单, 已写日志)。
void* resolve_steam_matchmaking(const cesium_safe::il2cpp_tbl& t, std::string* out_image, std::string* out_ns)
{
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",                 (const void*)t.domain_get },
        { "domain_get_assemblies",      (const void*)t.domain_get_assemblies },
        { "assembly_get_image",         (const void*)t.assembly_get_image },
        { "image_get_name",             (const void*)t.image_get_name },
        { "class_from_name",            (const void*)t.class_from_name },
        { "class_get_method_from_name", (const void*)t.class_get_method_from_name },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line("[steamhack] IL2CPP function table missing: " + missing + " - cannot resolve SteamMatchmaking");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] il2cpp domain is null, cannot resolve SteamMatchmaking");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] domain_get_assemblies returned null");
        return nullptr;
    }

    std::vector<void*> images;
    std::vector<std::string> image_names;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        images.push_back(image);
        image_names.push_back(cstr_or(t.image_get_name(image), "(null)"));
    }

    void* chosen = nullptr;
    std::string chosen_image;
    std::string chosen_ns;
    std::string all_images;
    for (size_t i = 0; i < images.size(); i++)
    {
        if (i) all_images += ", ";
        all_images += image_names[i];
        for (const char* ns : kMatchmakingNamespaces)
        {
            void* cls = t.class_from_name(images[i], ns, "SteamMatchmaking");
            if (!cls) continue;
            log_line("[steamhack] class SteamMatchmaking hit: image=" + image_names[i] +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " className=" + class_name_of(t, cls));
            // 多份命中时优先取 image 名里带 Steamworks 的那份(Steamworks.NET.dll 的 AOT 镜像)
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 image_names[i].find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = image_names[i]; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line("[steamhack] class SteamMatchmaking not found (candidate ns: \"Steamworks\" / global / \"Steamworks.NET\")"
                 " - stage 2 cannot be installed");
        log_line("[steamhack] domain assemblies " + std::to_string(image_names.size()) + " items: " + all_images);
    }
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

// 解析并挂钩 SteamMatchmaking.<method_name>(arg_count 个参数)。
// 顺序很重要: **先把默认返回值工厂解析并探测成功, 再挂钩** —— 解析/探测失败就完全不挂钩,
// 游戏行为与原版一致(不会因为打不开的绕过而变得更糟)。
bool install_lobby_hook(const cesium_safe::il2cpp_tbl& t,
                        const char* method_name, int arg_count,
                        void* detour, LPVOID* real_out,
                        void** target_out, void** method_out,
                        TaskFactory& f)
{
    std::string image;
    std::string ns;
    void* cls = resolve_steam_matchmaking(t, &image, &ns);
    if (!cls) return false;

    void* mi = t.class_get_method_from_name(cls, method_name, arg_count);
    if (!mi)
    {
        log_line("[steamhack] no hit for SteamMatchmaking." + std::string(method_name) + "(" +
                 std::to_string(arg_count) + " params): image=" + image + " ns=\"" + ns + "\"");
        dump_class_methods(t, cls, method_name);
        return false;
    }

    void* target = method_pointer_of(mi);
    const uint32_t pc = t.method_get_param_count ? t.method_get_param_count(mi) : (uint32_t)arg_count;
    log_line("[steamhack] hit SteamMatchmaking." + std::string(method_name) + ": image=" + image +
             " ns=\"" + ns + "\" MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)mi) +
             " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)target) + " (" + cesium::steam::diagnostics::gameassembly_rva(target) + ")" +
             " paramCount=" + std::to_string(pc));
    if (!target)
    {
        log_line("[steamhack] SteamMatchmaking." + std::string(method_name) +
                 " methodPointer is null (possibly not an AOT method) - skipping this hook");
        return false;
    }

    if (!resolve_task_factory(t, f, mi, method_name)) return false;

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, detour, real_out);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(" + std::string(method_name) + ") failed: " +
                 std::to_string((int)st) + " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ")");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(" + std::string(method_name) + ") failed: " +
                 std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    if (target_out) *target_out = target;
    if (method_out) *method_out = mi;
    log_line("[steamhack] SteamMatchmaking." + std::string(method_name) +
             " inline hook enabled (always returns a completed empty Task)");
    return true;
}

// =====================================================================================
// 阶段3: System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue -> 恒 false
// =====================================================================================
//
// 阶段2 之后实测: 异常从 CreateLobbyAsync **前进到了** Steamworks.Data.Lobby.SetPublic():
//     NullReferenceException
//     UI.UICom_CreateRoom.RequestCreateRoom ()
//     Steamworks.Data.Lobby.SetPublic ()
//     --- End of stack trace from previous location where exception was thrown ---
// 对应 decomp_full\UI\UICom_CreateRoom.cs:131-142 —— val.HasValue(第 133 行)被判成了 true,
// 于是进了 if 块, 到第 136 行 ((Lobby)(ref value)).SetPublic() 炸掉。
//
// 判据: 安装期探针里 get_Result() 返回**非空**装箱对象, 而"空 Nullable"装箱本应得 NULL
// (同一个 il2cpp_value_box 在零初始化缓冲区上确实返回了 NULL) —— 说明 il2cpp_runtime_invoke
// 把 Nullable<Lobby> 参数编组进 Task<T>..ctor(T) 时 hasValue 落成了非零。**不再去修 Task 的构造**。
//
// 修法: RequestCreateRoom 在**热更程序集、HybridCLR 解释执行**里, val.HasValue 是一次真实的
// 方法调用, 因此会 call 到 AOT 方法 System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue()。
// 把它换成恒返回 false: steamLobbyId 保持 0(协议里的"无 Steam 大厅"合法值), 第 142 行
// RequestCreateC2S 正常发出。语义自洽 —— Steam 不存在时任何 Lobby? 都不该 HasValue。
//
// 失败一律 fail-safe(**只记日志, 不挂钩**): 解析不到 get_HasValue、methodPointer 为空、
// MinHook 失败, 都只是"回到当前这个 NRE", 绝不让游戏更难启动。

// 【重要限制/风险提示】il2cpp 对泛型**值类型**可能只生成一份共享代码(Nullable<>.get_HasValue):
// 此时各实例(Nullable<int> / Nullable<Lobby> ...)的 MethodInfo 会指向同一个 methodPointer,
// "恒返回 false" 就会波及所有 Nullable<T>.HasValue。
// 本模块**故意不做**这个判定: 唯一可行的判定办法是拿开放泛型定义 System.Nullable`1 的
// 同名方法指针来比较, 而对泛型定义调用 il2cpp 的元数据 API 万一触发 il2cpp 自身的断言
// (abort) —— 那是 __try/__except **抓不到**的, 会直接把游戏打死。宁可"不判定"。
// 现有可用的信号(都无副作用, 见 Hook_NullableHasValue 的首次命中日志):
//   * 第 2 个寄存器参数 == 本实例的 MethodInfo -> 说明调用方在传具体实例的上下文,
//     与"共享泛型代码"一致; 反之则该实例看上去有自己的代码。
void log_hasvalue_scope_caution(const TaskFactory& f, void* target)
{
    log_line("[steamhack] " + f.param_class_name + ": get_HasValue target " +
             cesium::steam::diagnostics::hex((uintptr_t)target) + " RVA logged above; "
             "shared-generic scope **not determined** (metadata resolution on an open generic can trip an il2cpp assertion that cannot be caught, "
             "so it is skipped per fail-safe) - if this method is shared across Nullable<T> types, \"always false\" applies to them too");
}

// 解析 Nullable<Lobby>.get_HasValue 的 MethodInfo*(复用 resolve_task_factory 留下的 pcls)。
// 返回 nullptr = 解析失败(原因已写日志), 调用方**不挂钩**。
void* resolve_hasvalue_method(const cesium_safe::il2cpp_tbl& t, TaskFactory& f)
{
    if (f.hasvalue_resolved) return f.hasvalue_method;
    f.hasvalue_resolved = true;
    f.hasvalue_method = nullptr;

    if (!f.param_class)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": factory param class (Nullable) unresolved, cannot resolve get_HasValue - not hooked (fail-safe)");
        return nullptr;
    }
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": il2cpp_class_get_method_from_name export missing - cannot resolve get_HasValue, not hooked (fail-safe)");
        return nullptr;
    }

    // 【危险调用 #5】解析包在 POD + SEH 里(需求 #7): 违例只会导致"本次解析失败", 不会崩安装期。
    PodHasValueMethod r;
    pod_resolve_hasvalue_method(&t, f.param_class, &r);
    if (r.seh_code)
    {
        log_line("[steamhack] " + std::string(f.label) + ": resolve " + f.param_class_name +
                 "::get_HasValue() raised SEH " + hex_code_str(r.seh_code) +
                 " - captured by __try/__except, not hooked this time (fail-safe)");
        return nullptr;
    }
    if (!r.mi)
    {
        log_line("[steamhack] " + std::string(f.label) + ": class " + f.param_class_name +
                 " no get_HasValue() (0 params) hit - not hooked (fail-safe)");
        dump_all_methods(t, f.param_class, "Nullable.get_HasValue");
        return nullptr;
    }

    log_line("[steamhack] " + std::string(f.label) + ": hit " + f.param_class_name +
             "::get_HasValue() MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)r.mi) +
             " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)r.ptr) + " (" + cesium::steam::diagnostics::gameassembly_rva(r.ptr) + ")");

    if (!r.ptr)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": get_HasValue methodPointer is null (possibly not AOT-compiled) - **log only, no hook** (fail-safe)");
        return nullptr;
    }

    f.hasvalue_method = r.mi;
    return r.mi;
}

// 安装第 5 个 hook: Nullable<Lobby>.get_HasValue -> 恒 false。
bool install_hasvalue_hook(const cesium_safe::il2cpp_tbl& t, TaskFactory& f)
{
    void* mi = resolve_hasvalue_method(t, f);
    if (!mi) return false;

    void* target = method_pointer_of(mi);
    if (!target) return false;   // resolve_hasvalue_method 已记过日志

    // 纯提示: "恒 false" 在共享泛型代码下的影响范围无法安全判定(见函数注释), 只记一条日志。
    log_hasvalue_scope_caution(f, target);

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_NullableHasValue, (LPVOID*)&g_real_hasvalue);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(Nullable.get_HasValue) failed: " + std::to_string((int)st) +
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") - not hooked this time (fail-safe)");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(Nullable.get_HasValue) failed: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_hv_target = target;
    g_hv_method = mi;
    g_hv_hooked = true;
    f.hasvalue_ok = true;
    log_line("[steamhack] " + f.param_class_name +
             "::get_HasValue inline hook enabled (always false: any Lobby? counts as no value; "
             "game keeps LobbyId 0 -> RequestCreateC2S is used)");
    return true;
}

// =====================================================================================
// 方案B(安全网): Steamworks.Data.Lobby 上会抛 NRE 的方法 -> 无害
// =====================================================================================
//
// 为什么需要它: 方案C 之外的兜底。万一 direct 的 ABI 假设不成立、hasValue 仍被判成 true,
// 游戏就会执行 UICom_CreateRoom.cs:135-141 这段:
//     Lobby value = val.Value;
//     ((Lobby)(ref value)).SetPublic();                              // 136 行 <- 实测 NRE 炸在这里
//     ((Lobby)(ref value)).SetJoinable(true);                        // 138 行 <- 紧接着也会炸
//     steamLobbyId = SteamId.op_Implicit(((Lobby)(ref value)).Id);   // 140 行
// 这三个成员都是**非泛型的 Steamworks.Data.Lobby 值类型实例成员**, 不存在共享泛型代码问题,
// 被 HybridCLR 解释执行时会真实走到 AOT 入口(与阶段3 的 Nullable.HasValue 不同 —— 那个被解释器
// 当内建指令内联处理, hook 从未命中, 阶段3 的路线已实测作废)。
//   1) SetPublic()       -> no-op   签名 void(void* thisPtr)
//   2) SetJoinable(bool) -> no-op   签名 void(void* thisPtr, bool) —— **绝不解引用 thisPtr**
//   3) Id: 先判定是字段还是属性 ——
//        属性(存在 get_Id) -> 恒返回 0(SteamId 是 8 字节结构体, x64 下走 RAX 返回);
//        字段             -> **只记日志**(字段不可 hook), 不硬来。
// 目的: 即使 hasValue 仍为 true, 这两/三个调用也不再抛异常, 第 142 行
// RequestCreateC2S(..., steamLobbyId=0, ...) 照样执行。
// 失败一律只记日志(fail-safe): 解析不到类/方法、methodPointer 为空、MinHook 失败都只是"回到当前的
// NRE 状态", 绝不让安装期崩游戏。

using Fn_LobbyVoid = void (*)(void* self);
using Fn_LobbyBool = void (*)(void* self, bool joinable);
// get_Id 的返回类型是 SteamId(值类型, 8 字节): x64 ABI 下这种小结构体走 RAX 返回,
// 所以"恒返回 0"就是让 RAX=0 —— 用 void* 返回值声明即可。
// **尺寸在安装期核对过**(见 install_lobby_id): >8 字节的结构体走隐藏返回缓冲区, 那时返回 0 会写错
// 位置, 必须拒绝挂钩而不是硬来。
using Fn_LobbyGetId = void* (*)(void* self);

void* g_lsp_target = nullptr;   // Lobby.SetPublic 的原始 methodPointer
void* g_lsj_target = nullptr;   // Lobby.SetJoinable 的原始 methodPointer
void* g_lid_target = nullptr;   // Lobby.get_Id 的原始 methodPointer
void* g_lid_method = nullptr;   // Lobby.get_Id 的 MethodInfo*(诊断/日志)
Fn_LobbyVoid   g_real_lsp = nullptr;
Fn_LobbyBool   g_real_lsj = nullptr;
Fn_LobbyGetId  g_real_lid = nullptr;
bool g_lsp_hooked = false;
bool g_lsj_hooked = false;
bool g_lid_hooked = false;
std::atomic<long> g_lsp_hits{ 0 };
std::atomic<long> g_lsj_hits{ 0 };
std::atomic<long> g_lid_hits{ 0 };

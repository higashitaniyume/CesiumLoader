// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 方案B: Lobby.SetPublic / SetJoinable / Id 无害化 ----------
void Hook_LobbySetPublic(void* /*self*/)
{
    // no-op: 原实现会去调 SteamMatchmaking.SetLobbyType(Id, ...) 并在 Steam 未初始化时抛异常。
    // **故意不解引用 self** —— 什么也不做, 也就不需要它有效。
    if (g_lsp_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.SetPublic() intercepted on first hit -> no-op (no more NullReferenceException)");
}

void Hook_LobbySetJoinable(void* /*self*/, bool joinable)
{
    // no-op: 同理不解引用 self。joinable 是基元类型(bool 走第二个寄存器参数的低字节),
    // 记它是安全的 —— 但**不碰 self**。
    if (g_lsj_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.SetJoinable(" + std::string(joinable ? "true" : "false") +
                 ") intercepted on first hit -> no-op (no more NullReferenceException)");
}

void* Hook_LobbyGetId(void* /*self*/)
{
    // 恒返回 0 = SteamId 全零 -> 游戏侧 SteamId.op_Implicit(...) 得 0
    // -> 协议里"无 Steam 大厅"的合法值。
    if (g_lid_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.get_Id intercepted on first hit -> always returns 0 (zero SteamId; game's steamLobbyId=0)");
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
        log_line("[steamhack] plan B: IL2CPP function table missing (" + missing + ") - cannot resolve Steamworks.Data.Lobby");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] plan B: il2cpp domain is null, cannot resolve Lobby");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] plan B: domain_get_assemblies returned null");
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
            log_line("[steamhack] plan B: class Lobby hit: image=" + iname + " ns=\"" + std::string(ns) +
                     "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " className=" + class_name_of(t, cls));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line("[steamhack] plan B: class Steamworks.Data.Lobby not found (candidate ns: \"Steamworks.Data\" / "
                 "\"Steamworks\" / global / \"Steamworks.NET\")");
        log_line("[steamhack] plan B: domain assemblies " + std::to_string(count) + " items: " + all);
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
        log_line("[steamhack] Lobby field names (max 24): " +
                 (names.empty() ? std::string("(none)") : names));
    }
    else
    {
        log_line("[steamhack] Lobby field names: could not list"
                 " (il2cpp_class_get_fields or il2cpp_field_get_name export missing)");
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
        log_line("[steamhack] Lobby methods containing Id/Public/Join (max 24): " +
                 (names.empty() ? std::string("(none)") : names));
    }
    else
    {
        log_line("[steamhack] Lobby methods containing Id/Public/Join: could not list"
                 " (il2cpp_class_get_methods or il2cpp_method_get_name export missing)");
    }
}

// 解析并挂钩 Lobby 上的一个"无害化"方法(SetPublic / SetJoinable)。
bool install_lobby_noop(const cesium_safe::il2cpp_tbl& t, void* cls,
                        const char* method_name, int arg_count,
                        void* detour, LPVOID* real_out, void** target_out)
{
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] plan B: il2cpp_class_get_method_from_name export missing, cannot resolve Lobby." +
                 std::string(method_name));
        return false;
    }

    void* mi = t.class_get_method_from_name(cls, method_name, arg_count);
    if (!mi)
    {
        log_line("[steamhack] plan B: no hit for Lobby." + std::string(method_name) + "(" +
                 std::to_string(arg_count) + " params) - skipping this hook");
        return false;
    }

    void* target = method_pointer_of(mi);
    log_line("[steamhack] plan B: hit Lobby." + std::string(method_name) + " MethodInfo=" +
             cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)target) +
             " (" + cesium::steam::diagnostics::gameassembly_rva(target) + ")");
    if (!target)
    {
        log_line("[steamhack] plan B: Lobby." + std::string(method_name) +
                 " methodPointer is null (possibly not AOT-compiled) - **log only, no hook** (fail-safe)");
        return false;
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, detour, real_out);
    if (st != MH_OK)
    {
        log_line("[steamhack] plan B: MH_CreateHook(Lobby." + std::string(method_name) + ") failed: " +
                 std::to_string((int)st) + " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") - not hooked");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] plan B: MH_EnableHook(Lobby." + std::string(method_name) + ") failed: " +
                 std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    if (target_out) *target_out = target;
    log_line("[steamhack] plan B: Lobby." + std::string(method_name) +
             " inline hook enabled (no-op, no more NullReferenceException)");
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

    log_line("[steamhack] Lobby.Id check: il2cpp_class_get_field_from_name(\"Id\")=" +
             std::string(fld ? "hit -> **it is a field**" : "no hit -> not a field") +
             " | il2cpp_class_get_method_from_name(\"get_Id\", 0)=" +
             std::string(mi ? "hit -> **it is a property**" : "no hit -> no get_Id property"));

    // 交叉印证: 走属性 API 再确认一次(缺导出就跳过, 只记日志)
    if (t.class_get_property_from_name && t.property_get_get_method)
    {
        void* prop = t.class_get_property_from_name(cls, "Id");
        void* gm = prop ? t.property_get_get_method(prop) : nullptr;
        log_line("[steamhack] Lobby.Id cross-check: il2cpp_class_get_property_from_name(\"Id\")=" +
                 std::string(prop ? "hit" : "no hit") + " getter=" + cesium::steam::diagnostics::hex((uintptr_t)gm) +
                 (gm == mi ? " (consistent with get_Id)" : ""));
    }
    else
    {
        log_line("[steamhack] Lobby.Id cross-check: skipped"
                 " (il2cpp_class_get_property_from_name or il2cpp_property_get_get_method export missing)");
    }

    if (fld)
    {
        log_line("[steamhack] Lobby.Id verdict = **a field** (not a property) - fields cannot be hooked"
                 " (HybridCLR reads it by field offset when interpreting; there is no AOT entry to hook)."
                 "plan B **only logs Id, never forces it**; steamLobbyId is decided by plan C (which makes hasValue false "
                 "never reaches Id) and the hasValue verdict together");
        return false;
    }
    if (!mi)
    {
        log_line("[steamhack] Lobby.Id verdict = neither a field nor a get_Id() property - not hooked (fail-safe)");
        return false;
    }

    if (out_is_property) *out_is_property = true;

    void* target = method_pointer_of(mi);
    if (!target)
    {
        log_line("[steamhack] Lobby.get_Id methodPointer is null (possibly not AOT-compiled) - "
                 "**log only, no hook** (fail-safe)");
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

        log_line("[steamhack] Lobby.get_Id return type=" + rt_name +
                 " isValueType=" + (have_vt ? (is_vt ? "yes" : "no") : "undetermined") +
                 (have_sz ? (" size=" + std::to_string(sz) + " bytes")
                          : std::string(" size=(il2cpp_class_value_size export missing, not verified)")));

        if (have_vt && is_vt && have_sz && sz > 8)
        {
            log_line("[steamhack] Lobby.get_Id returns a struct of " + std::to_string(sz) +
                     " bytes (>8) - under the x64 ABI this uses a **hidden return buffer**; returning RAX=0 writes to the wrong place, "
                     "**not hooked** (fail-safe; plan C makes hasValue=false so this is never reached)");
            return false;
        }
        if (have_vt && !is_vt)
        {
            log_line("[steamhack] Lobby.get_Id returns a **reference type** (" + rt_name +
                     ") - returning 0 becomes a null reference (wrong semantics, may cause other exceptions), **not hooked** (fail-safe)");
            return false;
        }
        if (!have_sz)
            log_line("[steamhack] Lobby.get_Id: could not verify the returned struct size - continuing on the assumption"
                     "the \"SteamId is an 8-byte value type returned in RAX\" assumption (the only ABI assumption in plan B)");
    }
    else
    {
        log_line("[steamhack] Lobby.get_Id: cannot verify the return type (missing il2cpp_method_get_return_type / "
                 "il2cpp_type_get_name) - continuing on the \"SteamId is an 8-byte value type returned in RAX\" assumption");
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_LobbyGetId, (LPVOID*)&g_real_lid);
    if (st != MH_OK)
    {
        log_line("[steamhack] plan B: MH_CreateHook(Lobby.get_Id) failed: " + std::to_string((int)st) +
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") - not hooked");
        return false;
    }
    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] plan B: MH_EnableHook(Lobby.get_Id) failed: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_lid_target = target;
    g_lid_method = mi;
    g_lid_hooked = true;
    log_line("[steamhack] plan B: Lobby.get_Id inline hook enabled (always returns a zero SteamId -> steamLobbyId=0)");
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
        log_line("[steamhack] plan B: could not resolve class Steamworks.Data.Lobby - skipping Lobby method neutralization"
                 " (fail-safe, behavior unchanged)");
        return 0;
    }

    const bool cls_is_vt = (t.class_is_valuetype ? t.class_is_valuetype(cls) : false);
    log_line("[steamhack] plan B: using class Lobby image=" + image + " ns=\"" + ns +
             "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) + " className=" + class_name_of(t, cls) +
             " isValueType=" + (cls_is_vt ? "yes (struct)" : "no (class)"));

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

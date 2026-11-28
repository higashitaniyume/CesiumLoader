// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 阶段6: Steam 客户端属性兜底 (SteamApps.get_GameLanguage / SteamClient.get_SteamId) ----------
//
// 背景(国际服多开/无 Steam 启动崩溃根因):
//   阶段1 把 SteamManager.Awake() 挂成 no-op 后, Facepunch.Steamworks 未初始化
//   (SteamClient.IsValid == false, SteamApps.Internal == null, SteamUser.Internal == null)。
//   国际服登录进入大厅时, FlowSDK.dll (Kotlin/Native) 调回 Unity 主线程:
//     MaxLoom.Update -> MaxLibUnity.sendFn -> MaxContextUnity.Send
//       -> MaxContextPlatformSteam.ChargeInit -> MaxContextPlatformSteam.GetCurrentLanguage
//   该方法连续调用两个无判空的 Facepunch.Steamworks 静态属性:
//     1) Steamworks.SteamApps.get_GameLanguage() -> SteamApps.Internal 为 null 时抛 NullReferenceException
//     2) Steamworks.SteamClient.get_SteamId()   -> SteamUser.Internal 为 null 时抛 NullReferenceException
//   未捕获的 IL2CPP C++ 异常直接穿过 FlowSDK.dll 栈帧展开, 跳过 Kotlin/Native LeaveFrame,
//   随后后台 GC (ConcurrentMarkAndSweep) 扫描损坏的 shadow stack 触发 0xC0000005。
//
// 修法:
//   挂钩这两个 0 参数静态 getter; 仅在 SteamClient.IsValid == false (或无法判断) 时返回安全兜底值
//   ("english" / SteamId(0)), 若 SteamClient.IsValid == true 则透传原始实现。

using Fn_SteamAppsGetGameLanguage = void* (*)(void* method_info);
using Fn_SteamClientGetSteamId = uint64_t (*)(void* method_info);
using Fn_SteamClientGetIsValid = uint8_t (*)(void* method_info);

Fn_SteamAppsGetGameLanguage g_real_sa_game_lang = nullptr;
void* g_sa_lang_target = nullptr;
void* g_sa_lang_method = nullptr;
bool  g_sa_lang_hooked = false;
std::atomic<long> g_sa_lang_hits{ 0 };
void* g_sa_lang_str = nullptr;         // 预建好的 Il2CppString* ("english")
uint32_t g_sa_lang_gchandle = 0;       // 显式 GC root 句柄(0 = 未持有/导出缺失)

Fn_SteamClientGetSteamId g_real_sc_steam_id = nullptr;
void* g_sc_id_target = nullptr;
void* g_sc_id_method = nullptr;
bool  g_sc_id_hooked = false;
std::atomic<long> g_sc_id_hits{ 0 };

Fn_SteamClientGetIsValid g_sc_isvalid_fn = nullptr;
void* g_sc_isvalid_method = nullptr;

// ---------- POD + SEH 安全边界(只允许 POD, 不得含带析构函数的 C++ 对象) ----------

void pod_call_string_new(void* string_new_fn, const char* utf8, void** out_str, unsigned long* out_seh)
{
    *out_str = nullptr;
    *out_seh = 0;
    if (!string_new_fn || !utf8) return;

    __try
    {
        typedef void* (*StringNewFn)(const char*);
        *out_str = ((StringNewFn)string_new_fn)(utf8);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_str = nullptr;
    }
}

void pod_call_bool_getter(void* fn_ptr, void* method_info, uint8_t* out_val, unsigned long* out_seh)
{
    *out_val = 0;
    *out_seh = 0;
    if (!fn_ptr) return;

    __try
    {
        typedef uint8_t (*BoolGetterFn)(void*);
        *out_val = ((BoolGetterFn)fn_ptr)(method_info);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_val = 0;
    }
}

// ---------- 运行期 IsValid 透传判定与 Detour ----------

bool steam_client_is_valid_now()
{
    if (!g_sc_isvalid_fn) return false;
    uint8_t val = 0;
    unsigned long seh = 0;
    pod_call_bool_getter((void*)g_sc_isvalid_fn, g_sc_isvalid_method, &val, &seh);
    return seh == 0 && val != 0;
}

void* Hook_SteamAppsGetGameLanguage(void* method_info)
{
    if (g_real_sa_game_lang && steam_client_is_valid_now())
        return g_real_sa_game_lang(method_info);

    if (g_sa_lang_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] SteamApps.get_GameLanguage intercepted on first hit "
                 "(SteamClient.IsValid=false) -> returning fallback \"english\"");
    return g_sa_lang_str;
}

uint64_t Hook_SteamClientGetSteamId(void* method_info)
{
    if (g_real_sc_steam_id && steam_client_is_valid_now())
        return g_real_sc_steam_id(method_info);

    if (g_sc_id_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] SteamClient.get_SteamId intercepted on first hit "
                 "(SteamClient.IsValid=false) -> returning fallback SteamId(0)");
    return 0;
}

// ---------- Steamworks 根命名空间类解析与 Hook 安装 ----------

const char* kSteamworksRootNamespaces[] = { "Steamworks", "Steamworks.NET", "" };

void* resolve_steamworks_root_class(const cesium_safe::il2cpp_tbl& t, const char* class_name,
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
        log_line("[steamhack] stage 6: IL2CPP function table missing (" + missing +
                 ") - cannot resolve " + class_name);
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] stage 6: il2cpp domain is null - cannot resolve " + std::string(class_name));
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] stage 6: domain_get_assemblies returned null");
        return nullptr;
    }

    void* chosen = nullptr;
    std::string chosen_image;
    std::string chosen_ns;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        const std::string iname = cstr_or(t.image_get_name(image), "(null)");

        for (const char* ns : kSteamworksRootNamespaces)
        {
            void* cls = t.class_from_name(image, ns, class_name);
            if (!cls) continue;
            log_line("[steamhack] stage 6: class hit " + std::string(class_name) + ": image=" + iname +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " className=" + class_name_of(t, cls));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;
        }
    }

    if (!chosen)
        log_line("[steamhack] stage 6: class not found " + std::string(class_name) +
                 " (candidate ns: \"Steamworks\" / \"Steamworks.NET\" / global)");
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

void resolve_steam_client_is_valid(const cesium_safe::il2cpp_tbl& t, void* steam_client_cls)
{
    g_sc_isvalid_fn = nullptr;
    g_sc_isvalid_method = nullptr;
    if (!steam_client_cls || !t.class_get_method_from_name) return;

    void* mi = t.class_get_method_from_name(steam_client_cls, "get_IsValid", 0);
    if (!mi)
    {
        log_line("[steamhack] stage 6: SteamClient.get_IsValid(0) not found (always returning fallback when hooked)");
        return;
    }
    void* ptr = method_pointer_of(mi);
    if (!ptr)
    {
        log_line("[steamhack] stage 6: SteamClient.get_IsValid methodPointer is null (always returning fallback when hooked)");
        return;
    }
    g_sc_isvalid_fn = reinterpret_cast<Fn_SteamClientGetIsValid>(ptr);
    g_sc_isvalid_method = mi;
    log_line("[steamhack] stage 6: SteamClient.get_IsValid resolved MethodInfo=" +
             cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" +
             cesium::steam::diagnostics::hex((uintptr_t)ptr) + " (" +
             cesium::steam::diagnostics::gameassembly_rva(ptr) + ")");
}

bool install_steam_game_language_hook(const cesium_safe::il2cpp_tbl& t)
{
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] stage 6: il2cpp_class_get_method_from_name missing - skipping SteamApps.get_GameLanguage");
        return false;
    }
    if (!t.string_new)
    {
        log_line("[steamhack] stage 6: il2cpp_string_new export missing - cannot build fallback string, skipping SteamApps.get_GameLanguage");
        return false;
    }

    std::string image, ns;
    void* cls = resolve_steamworks_root_class(t, "SteamApps", &image, &ns);
    if (!cls) return false;

    void* mi = t.class_get_method_from_name(cls, "get_GameLanguage", 0);
    if (!mi)
    {
        log_line("[steamhack] stage 6: SteamApps.get_GameLanguage(0) not found - skipping");
        return false;
    }
    void* target = method_pointer_of(mi);
    log_line("[steamhack] stage 6: hit SteamApps.get_GameLanguage MethodInfo=" +
             cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" +
             cesium::steam::diagnostics::hex((uintptr_t)target) + " (" +
             cesium::steam::diagnostics::gameassembly_rva(target) + ")");
    if (!target)
    {
        log_line("[steamhack] stage 6: SteamApps.get_GameLanguage methodPointer is null - not hooked");
        return false;
    }

    // 返回值核对: 预期是 System.String 引用类型; 若被判定为值类型则拒绝挂钩。
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
        log_line("[steamhack] stage 6: SteamApps.get_GameLanguage return type=" + rt_name +
                 " isValueType=" + (have_vt ? (is_vt ? "yes" : "no") : "undetermined"));
        if (have_vt && is_vt)
        {
            log_line("[steamhack] stage 6: SteamApps.get_GameLanguage unexpectedly returns a value type - not hooked (fail-safe)");
            return false;
        }
    }

    // 预建并固定 "english" 托管字符串, 绝不在 hook 内返回 nullptr。
    void* str = nullptr;
    unsigned long seh = 0;
    pod_call_string_new((void*)t.string_new, "english", &str, &seh);
    if (seh || !str)
    {
        log_line("[steamhack] stage 6: il2cpp_string_new(\"english\") failed" +
                 (seh ? (" (SEH " + hex_code_str(seh) + ")") : " (returned null)") +
                 " - not hooked (fail-safe)");
        return false;
    }

    uint32_t handle = 0;
    if (t.gchandle_new)
    {
        unsigned long hseh = 0;
        pod_gchandle_new((void*)t.gchandle_new, str, &handle, &hseh);
        if (hseh)
            log_line("[steamhack] stage 6[GC root]: il2cpp_gchandle_new raised SEH " + hex_code_str(hseh) +
                     " - relying on module global g_sa_lang_str only");
    }

    g_sa_lang_str = str;
    g_sa_lang_gchandle = handle;
    log_line("[steamhack] stage 6[GC root]: fallback \"english\" Il2CppString=" +
             cesium::steam::diagnostics::hex((uintptr_t)g_sa_lang_str) +
             (g_sa_lang_gchandle ? (" gchandle=" + std::to_string(g_sa_lang_gchandle))
                                 : std::string(" (global storage only)")));

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_SteamAppsGetGameLanguage, (LPVOID*)&g_real_sa_game_lang);
    if (st != MH_OK)
    {
        log_line("[steamhack] stage 6: MH_CreateHook(SteamApps.get_GameLanguage) failed: " + std::to_string((int)st));
        return false;
    }
    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] stage 6: MH_EnableHook(SteamApps.get_GameLanguage) failed: " + std::to_string((int)st));
        MH_RemoveHook(target);
        g_real_sa_game_lang = nullptr;
        return false;
    }

    g_sa_lang_target = target;
    g_sa_lang_method = mi;
    g_sa_lang_hooked = true;
    log_line("[steamhack] stage 6: SteamApps.get_GameLanguage inline hook enabled");
    return true;
}

bool install_steam_client_id_hook(const cesium_safe::il2cpp_tbl& t, void* steam_client_cls)
{
    if (!steam_client_cls || !t.class_get_method_from_name) return false;

    void* mi = t.class_get_method_from_name(steam_client_cls, "get_SteamId", 0);
    if (!mi)
    {
        log_line("[steamhack] stage 6: SteamClient.get_SteamId(0) not found - skipping");
        return false;
    }
    void* target = method_pointer_of(mi);
    log_line("[steamhack] stage 6: hit SteamClient.get_SteamId MethodInfo=" +
             cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" +
             cesium::steam::diagnostics::hex((uintptr_t)target) + " (" +
             cesium::steam::diagnostics::gameassembly_rva(target) + ")");
    if (!target)
    {
        log_line("[steamhack] stage 6: SteamClient.get_SteamId methodPointer is null - not hooked");
        return false;
    }

    // 返回值 ABI 安全门(同 Lobby.get_Id): 仅当 <= 8 字节值类型时可直接 RAX 返回 0。
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

        log_line("[steamhack] stage 6: SteamClient.get_SteamId return type=" + rt_name +
                 " isValueType=" + (have_vt ? (is_vt ? "yes" : "no") : "undetermined") +
                 (have_sz ? (" size=" + std::to_string(sz) + " bytes")
                          : std::string(" size=(il2cpp_class_value_size missing, not verified)")));

        if (have_vt && is_vt && have_sz && sz > 8)
        {
            log_line("[steamhack] stage 6: SteamClient.get_SteamId returns a struct of " + std::to_string(sz) +
                     " bytes (>8) - uses hidden return buffer under x64 ABI, not hooked (fail-safe)");
            return false;
        }
        if (have_vt && !is_vt)
        {
            log_line("[steamhack] stage 6: SteamClient.get_SteamId returns a reference type (" + rt_name +
                     ") - returning 0 would be null reference, not hooked (fail-safe)");
            return false;
        }
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_SteamClientGetSteamId, (LPVOID*)&g_real_sc_steam_id);
    if (st != MH_OK)
    {
        log_line("[steamhack] stage 6: MH_CreateHook(SteamClient.get_SteamId) failed: " + std::to_string((int)st));
        return false;
    }
    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] stage 6: MH_EnableHook(SteamClient.get_SteamId) failed: " + std::to_string((int)st));
        MH_RemoveHook(target);
        g_real_sc_steam_id = nullptr;
        return false;
    }

    g_sc_id_target = target;
    g_sc_id_method = mi;
    g_sc_id_hooked = true;
    log_line("[steamhack] stage 6: SteamClient.get_SteamId inline hook enabled");
    return true;
}

int install_steam_client_getters(const cesium_safe::il2cpp_tbl& t, int* out_expected)
{
    if (out_expected) *out_expected = 2;
    int installed = 0;

    std::string sc_image, sc_ns;
    void* sc_cls = resolve_steamworks_root_class(t, "SteamClient", &sc_image, &sc_ns);
    if (sc_cls)
        resolve_steam_client_is_valid(t, sc_cls);

    if (install_steam_game_language_hook(t)) installed++;
    if (sc_cls && install_steam_client_id_hook(t, sc_cls)) installed++;
    return installed;
}

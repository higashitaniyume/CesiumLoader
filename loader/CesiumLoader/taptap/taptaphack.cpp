// taptaphack.cpp - TapTap 版绕过实现
//
// 两个 hook:
//   1. TapSDK.Core.Standalone.TapClientStandalone.IsLaunchedFromTapTapPC()
//      TapSDK.Core.Standalone.TapCoreStandalone.IsLaunchedFromTapTapPC()
//      -> 恒返回一个常驻的已完成 Task<bool> = true 的对象(构造一次, 两处复用)
//      门禁路径是 IsLaunchedFromTapTapPC -> RunClientBridgeMethod, 它要求真机启动
//      于 TapTap PC 客户端(拿不到 -> needQuitGame=true -> 对话框“获取游戏信息失败”)。
//      绕过只是让游戏按“已从 TapTap 客户端启动”继续走启动流程。
//   2. taptap_api.dll!TapSDK_RestartAppIfNecessary() -> 恒返回 0
//      (与 Steam 的 SteamAPI_RestartAppIfNecessary 是同槽位)
//
// MinHook 所有权归 speedhack.cpp, 本模块只挂自己的 hook; 任何解析/调用失败都只记日志,
// 绝不阻止游戏启动。

#include "taptap/taptaphack.h"

#include "platform/loader.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <filesystem>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <MinHook.h>

namespace fs = std::filesystem;

#include "steam/steam_diagnostics.h"

namespace
{

// ---------- 状态 ----------
bool g_installed = false;
int g_hook_count = 0;
bool g_mh_ready = false;

bool ensure_minhook()
{
    if (g_mh_ready) return true;
    MH_STATUS st = MH_Initialize();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED)
    {
        log_line("[taptaphack] MinHook init failed: " + std::to_string((int)st));
        return false;
    }
    g_mh_ready = true;
    return true;
}

// ---------- 查找 ----------
struct MethodHit
{
    void* method = nullptr;   // MethodInfo*
    std::string image;
    std::string ns;
};

inline void* method_pointer_of(void* mi) { return mi ? *reinterpret_cast<void**>(mi) : nullptr; }
inline const char* cstr_or(const char* s, const char* fb) { return s ? s : fb; }

MethodHit find_method_anywhere(const cesium_safe::il2cpp_tbl& t,
                               const char* cls_name, const char* meth_name,
                               const std::vector<std::string>& candidate_namespaces)
{
    if (!t.domain_get_assemblies || !t.assembly_get_image || !t.image_get_name ||
        !t.domain_get || !t.class_from_name || !t.class_get_methods ||
        !t.method_get_name || !t.method_get_param_count)
        return {};

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(t.domain_get(), &count);
    if (!assemblies) return {};

    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        const char* image_name = t.image_get_name(image);

        for (const std::string& ns : candidate_namespaces)
        {
            void* cls = t.class_from_name(image, ns.c_str(), cls_name);
            if (!cls) continue;

            void* iter = nullptr;
            for (;;)
            {
                void* mm = t.class_get_methods(cls, &iter);
                if (!mm) break;
                const char* mn = t.method_get_name(mm);
                if (!mn || strcmp(mn, meth_name) != 0) continue;
                if (t.method_get_param_count(mm) != 0) continue;

                MethodHit hit;
                hit.method = mm;
                hit.image = image_name ? image_name : "(null)";
                hit.ns = ns;
                return hit;
            }
        }
    }
    return {};
}

// ---------- Task<bool>(true) ----------
void* g_task_true = nullptr;
void* g_task_true_class = nullptr;
uint32_t g_task_true_gchandle = 0;
bool g_task_ready = false;

void* Hook_IsLaunchedFromTapTapPC(void* /*this_ptr*/, void* /*method_info*/)
{
    return g_task_true;
}

// 按**参数类型**精确挑出 Task<bool>::.ctor(Boolean)。筛不到就放弃。
void* find_task_bool_ctor(const cesium_safe::il2cpp_tbl& t, void* cls, void* bool_cls)
{
    if (!cls || !bool_cls || !t.class_get_methods || !t.method_get_name ||
        !t.method_get_param_count || !t.method_get_param || !t.type_get_object || !t.class_from_system_type)
        return nullptr;

    void* iter = nullptr;
    for (;;)
    {
        void* m = t.class_get_methods(cls, &iter);
        if (!m) break;
        const char* n = t.method_get_name(m);
        if (!n || strcmp(n, ".ctor") != 0) continue;
        if (t.method_get_param_count(m) != 1) continue;
        void* pt = t.method_get_param(m, 0);
        if (!pt) continue;
        void* pto = t.type_get_object(pt);
        void* pcls = pto ? t.class_from_system_type(pto) : nullptr;
        if (pcls == bool_cls) return m;
    }
    return nullptr;
}

// 构造常驻的已完成 Task<bool>(true)。从目标方法的返回类型推导 Task<bool> 的 Il2CppClass,
// Task<bool> 与 Task<Lobby[]> 共用同一套语义: ctor(Boolean) 之后 await 立刻返回 true。
// 失败只记日志、返回 false, 由调用方 fail-safe。
bool ensure_task_true(const cesium_safe::il2cpp_tbl& t, void* sample_method)
{
    if (g_task_ready) return true;

    struct { const char* n; const void* p; } need[] = {
        { "method_get_return_type",     (const void*)t.method_get_return_type },
        { "type_get_object",            (const void*)t.type_get_object },
        { "class_from_system_type",     (const void*)t.class_from_system_type },
        { "object_new",                 (const void*)t.object_new },
        { "value_box",                  (const void*)t.value_box },
        { "class_get_methods",          (const void*)t.class_get_methods },
        { "method_get_name",            (const void*)t.method_get_name },
        { "method_get_param_count",     (const void*)t.method_get_param_count },
        { "method_get_param",           (const void*)t.method_get_param },
        { "runtime_invoke",             (const void*)t.runtime_invoke },
        { "get_corlib",                 (const void*)t.get_corlib },
        { "class_from_name",            (const void*)t.class_from_name },
        { "type_get_name",              (const void*)t.type_get_name },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty()) return false;

    void* ret = t.method_get_return_type(sample_method);
    if (!ret) return false;
    void* tobj = t.type_get_object(ret);
    void* task_cls = tobj ? t.class_from_system_type(tobj) : nullptr;
    const std::string ret_name = t.type_get_name ? cstr_or(t.type_get_name(ret), "(null)") : std::string("(null)");
    if (!task_cls || ret_name.find("Task") == std::string::npos) return false;

    void* corlib = t.get_corlib();
    void* bool_cls = corlib ? t.class_from_name(corlib, "System", "Boolean") : nullptr;
    if (!bool_cls) return false;

    void* ctor = find_task_bool_ctor(t, task_cls, bool_cls);
    if (!ctor) return false;
    void* ctor_ptr = method_pointer_of(ctor);
    if (!ctor_ptr) return false;

    // box(true)
    unsigned char one = 1;
    void* boxed_true = t.value_box ? t.value_box(bool_cls, &one) : nullptr;
    if (!boxed_true) return false;

    // void* obj = il2cpp_object_new(task_cls); ctor(obj, boxed);
    void* obj = t.object_new(task_cls);
    if (!obj) return false;
    void* args[1] = { boxed_true };
    void* exc = nullptr;
    t.runtime_invoke(ctor, obj, args, &exc);
    if (exc)
    {
        log_line("[taptaphack] Task<bool> construction raised a managed exception");
        return false;
    }

    if (t.gchandle_new)
    {
        uint32_t gh = t.gchandle_new(obj, /*pinned=*/false);
        if (gh != 0) g_task_true_gchandle = gh;
    }

    g_task_true = obj;
    g_task_true_class = task_cls;
    g_task_ready = true;
    return true;
}

// ---------- TapSDK_RestartAppIfNecessary ----------
using Fn_RestartApp = int (*)(unsigned int);
Fn_RestartApp g_real_restart = nullptr;
void* g_restart_target = nullptr;
bool g_restart_hooked = false;

int Hook_RestartApp(unsigned int /*appid*/)
{
    return 0;
}

std::wstring taptap_api_path()
{
    wchar_t buf[1024];
    DWORD n = GetModuleFileNameW(nullptr, buf, 1024);
    if (n == 0 || n >= 1024) return L"";
    std::wstring exe(buf, n);
    size_t pos = exe.find_last_of(L"\\/");
    std::wstring dir = (pos == std::wstring::npos) ? L"." : exe.substr(0, pos);

    std::error_code ec;
    for (fs::directory_iterator it(dir, ec), end; !ec && it != end; it.increment(ec))
    {
        std::error_code ec2;
        if (!it->is_directory(ec2) || ec2) continue;
        const std::string name = it->path().filename().string();
        if (name.size() < 5 || name.compare(name.size() - 5, 5, "_Data") != 0) continue;
        std::wstring cand = it->path().wstring() + L"\\Plugins\\x86_64\\taptap_api.dll";
        if (fs::exists(cand, ec2)) return cand;
    }
    return std::wstring();
}

bool install_restart_check()
{
    std::wstring path = taptap_api_path();
    if (path.empty())
    {
        log_line("[taptaphack] cannot derive the TapTap SDK directory, skipping TapSDK_RestartAppIfNecessary");
        return false;
    }

    HMODULE mod = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!mod) mod = GetModuleHandleW(L"taptap_api.dll");
    if (!mod) return false;

    void* target = reinterpret_cast<void*>(GetProcAddress(mod, "TapSDK_RestartAppIfNecessary"));
    if (!target) return false;

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHookApi(L"taptap_api.dll", "TapSDK_RestartAppIfNecessary",
                                    (LPVOID)&Hook_RestartApp, (LPVOID*)&g_real_restart);
    if (st != MH_OK) return false;
    st = MH_EnableHook(target);
    if (st != MH_OK) { MH_RemoveHook(target); return false; }

    g_restart_target = target;
    g_restart_hooked = true;
    return true;
}

// ---------- IsLaunchedFromTapTapPC 接线 ----------
void* g_launched_task_target_1 = nullptr;
void* g_launched_task_target_2 = nullptr;
void* g_launched_method_1 = nullptr;
void* g_launched_method_2 = nullptr;
bool g_launched_hooked_1 = false;
bool g_launched_hooked_2 = false;

bool install_launched_hook(const cesium_safe::il2cpp_tbl& t, const MethodHit& hit,
                           int which, void** out_target, void** out_method)
{
    if (!ensure_minhook()) return false;
    if (!ensure_task_true(t, hit.method)) return false;

    void* target = method_pointer_of(hit.method);
    if (!target) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_IsLaunchedFromTapTapPC,
                                 which == 1 ? (LPVOID*)&g_launched_method_1
                                            : (LPVOID*)&g_launched_method_2);
    if (st != MH_OK) return false;
    st = MH_EnableHook(target);
    if (st != MH_OK) { MH_RemoveHook(target); return false; }

    *out_target = target;
    return true;
}

} // namespace

// =====================================================================================
// 对外接口
// =====================================================================================

bool taptaphack_install(const cesium_safe::il2cpp_tbl& t,
                        bool bypass_launched,
                        bool bypass_restart_check,
                        bool bypass_login)
{
    if (g_installed) return g_hook_count > 0;
    g_installed = true;

    log_line("[taptaphack] installing TapTap bypass ...");
    int hooks = 0;

    const std::vector<std::string> kCandidates = {
        "", "TapTap", "TapSDK", "TapSDK.Core", "TapSDK.Core.Standalone",
        "TapSDK.Core.Runtime", "TapSDK.Login", "TapSDK.Login.Standalone",
        "TapSDK.Login.Runtime", "BnSdk", "Platform", "Game", "Client"
    };

    // 阶段1: IsLaunchedFromTapTapPC 恒通过(两个同名同签名实现都要挂)
    if (bypass_launched)
    {
        MethodHit a = find_method_anywhere(t, "TapClientStandalone", "IsLaunchedFromTapTapPC", kCandidates);
        MethodHit b = find_method_anywhere(t, "TapCoreStandalone", "IsLaunchedFromTapTapPC", kCandidates);

        if (a.method)
        {
            if (install_launched_hook(t, a, 1, &g_launched_task_target_1, &g_launched_method_1))
            {
                g_launched_hooked_1 = true;
                hooks++;
                log_line("[taptaphack] IsLaunchedFromTapTapPC hooked (class=TapClientStandalone, image=" + a.image + ", ns=\"" + a.ns + "\")");
            }
        }
        else
        {
            log_line("[taptaphack] IsLaunchedFromTapTapPC not found in TapClientStandalone");
        }

        if (b.method)
        {
            if (install_launched_hook(t, b, 2, &g_launched_task_target_2, &g_launched_method_2))
            {
                g_launched_hooked_2 = true;
                hooks++;
                log_line("[taptaphack] IsLaunchedFromTapTapPC hooked (class=TapCoreStandalone, image=" + b.image + ", ns=\"" + b.ns + "\")");
            }
        }
        else
        {
            log_line("[taptaphack] IsLaunchedFromTapTapPC not found in TapCoreStandalone");
        }
    }
    else
    {
        log_line("[taptaphack] tapTapBypassEnabled=false, skipping IsLaunchedFromTapTapPC (game behaves as vanilla)");
    }

    // 阶段2: TapSDK_RestartAppIfNecessary 恒 0
    if (bypass_restart_check)
    {
        if (install_restart_check()) hooks++;
    }

    // login stubs: 签名未钉死, 仅留开关与日志(实证后实现)。
    if (bypass_login)
    {
        log_line("[taptaphack] tapTapBypassLogin=true but login stubs are not implemented yet; logged only");
    }

    g_hook_count = hooks;
    log_line("[taptaphack] done: enabled " + std::to_string(hooks) + " hook(s)");
    return hooks > 0;
}

int taptaphack_hook_count()
{
    return g_hook_count;
}

void taptaphack_uninstall()
{
    if (!g_mh_ready) return;

    if (g_launched_hooked_1 && g_launched_task_target_1)
    {
        MH_DisableHook(g_launched_task_target_1);
        MH_RemoveHook(g_launched_task_target_1);
        g_launched_hooked_1 = false;
        g_launched_task_target_1 = nullptr;
    }
    if (g_launched_hooked_2 && g_launched_task_target_2)
    {
        MH_DisableHook(g_launched_task_target_2);
        MH_RemoveHook(g_launched_task_target_2);
        g_launched_hooked_2 = false;
        g_launched_task_target_2 = nullptr;
    }
    if (g_restart_hooked && g_restart_target)
    {
        MH_DisableHook(g_restart_target);
        MH_RemoveHook(g_restart_target);
        g_restart_hooked = false;
        g_restart_target = nullptr;
    }

    g_installed = false;
    g_hook_count = 0;
    g_mh_ready = false;
}

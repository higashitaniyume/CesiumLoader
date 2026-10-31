// il2cpp_boot.cpp - IL2CPP 桥: 导出解析 / 域就绪 / HybridCLR 检测 / Assembly.Load(byte[]) / 入口调用
//
// 从 loader.cpp 拆出(纯移动): 这里只做"把原生 API 用对"的事, 不含配置/控制台/变速/mod 编排。
// 函数表与安全封装函数表都只有一份定义(本文件), 通过 runtime/loader_internal.h 暴露给引导线程。
// 所有日志文本、错误字符串与原实现逐字一致 —— 它们记录了逆向结论, 不要改写。

#include "runtime/loader_internal.h"

#include "platform/loader.h"

#include <tlhelp32.h>   // 等待 GameAssembly.dll 用(模块枚举)
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

// IL2CPP 安全封装的运行时错误缓冲(定义)
namespace cesium_safe { std::string g_last_error; }

namespace cesium::runtime::boot
{

namespace
{

// IL2CPP 函数表(由 resolve_il2cpp 填充)
Il2Cpp g_il2cpp;

// IL2CPP 安全封装用的函数表(由 resolve_il2cpp 填充)
cesium_safe::il2cpp_tbl g_safe_il2cpp;

} // namespace

// ---------- 工具 ----------

static void* load_symbol(HMODULE module, const char* name)
{
    return reinterpret_cast<void*>(GetProcAddress(module, name));
}

// ---------- 进程/模块查找 ----------

static HMODULE find_module(const wchar_t* name)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, 0);
    if (snap == INVALID_HANDLE_VALUE) return nullptr;
    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    HMODULE result = nullptr;
    if (Module32FirstW(snap, &entry))
    {
        do
        {
            if (_wcsicmp(entry.szModule, name) == 0)
            {
                result = reinterpret_cast<HMODULE>(entry.modBaseAddr);
                break;
            }
        } while (Module32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return result;
}

HMODULE wait_game_assembly(DWORD timeout_secs)
{
    ULONGLONG end = GetTickCount64() + (ULONGLONG)timeout_secs * 1000;
    for (;;)
    {
        HMODULE m = find_module(L"GameAssembly.dll");
        if (m) return m;
        if (GetTickCount64() >= end) return nullptr;
        Sleep(200);
    }
}

// 取 Unity 的 Application.version(bundleVersion, 例如 "3.3.0") —— 游戏版本。
// 纯展示信息: 找不到 UnityEngine.CoreModule / 方法 / 调用失败都只返回空串,
// 绝不因为"读不到版本号"影响引导。
std::string application_version(Il2CppDomain* domain)
{
    void* image = nullptr;
    size_t count = 0;
    Il2CppAssembly** asms = g_il2cpp.domain_get_assemblies(domain, &count);
    for (size_t i = 0; asms != nullptr && i < count; i++)
    {
        void* img = g_il2cpp.assembly_get_image(asms[i]);
        if (!img) continue;
        const char* name = g_il2cpp.image_get_name(img);
        if (name && strcmp(name, "UnityEngine.CoreModule") == 0) { image = img; break; }
    }
    if (!image) return std::string();

    Il2CppClass* app = g_il2cpp.class_from_name(image, "UnityEngine", "Application");
    if (!app) return std::string();

    Il2CppMethod* method = g_il2cpp.class_get_method_from_name(app, "get_version", 0);
    if (!method) return std::string();

    cesium_safe::g_last_error.clear();
    return cesium_safe::invoke_string(g_safe_il2cpp, method, nullptr);
}

bool resolve_il2cpp(HMODULE game_assembly)
{
    auto* p = g_il2cpp.domain_get = reinterpret_cast<Il2CppDomain* (*)()>(load_symbol(game_assembly, "il2cpp_domain_get"));
    if (!p) return false;
    g_il2cpp.assembly_get_image = reinterpret_cast<void* (*)(Il2CppAssembly*)>(load_symbol(game_assembly, "il2cpp_assembly_get_image"));
    g_il2cpp.image_get_assembly = reinterpret_cast<Il2CppAssembly* (*)(void*)>(load_symbol(game_assembly, "il2cpp_image_get_assembly"));
    g_il2cpp.class_from_name = reinterpret_cast<Il2CppClass* (*)(void*, const char*, const char*)>(load_symbol(game_assembly, "il2cpp_class_from_name"));
    g_il2cpp.class_get_method_from_name = reinterpret_cast<Il2CppMethod* (*)(Il2CppClass*, const char*, int)>(load_symbol(game_assembly, "il2cpp_class_get_method_from_name"));
    g_il2cpp.class_get_methods = reinterpret_cast<Il2CppMethod* (*)(Il2CppClass*, void**)>(load_symbol(game_assembly, "il2cpp_class_get_methods"));
    g_il2cpp.method_get_name = reinterpret_cast<const char* (*)(Il2CppMethod*)>(load_symbol(game_assembly, "il2cpp_method_get_name"));
    g_il2cpp.method_get_param = reinterpret_cast<void* (*)(Il2CppMethod*, uint32_t)>(load_symbol(game_assembly, "il2cpp_method_get_param"));
    g_il2cpp.method_get_param_count = reinterpret_cast<uint32_t (*)(Il2CppMethod*)>(load_symbol(game_assembly, "il2cpp_method_get_param_count"));
    g_il2cpp.runtime_invoke = reinterpret_cast<Il2CppObject* (*)(Il2CppMethod*, Il2CppObject*, void**, Il2CppException**)>(load_symbol(game_assembly, "il2cpp_runtime_invoke"));
    g_il2cpp.thread_attach = reinterpret_cast<Il2CppThread* (*)(Il2CppDomain*)>(load_symbol(game_assembly, "il2cpp_thread_attach"));
    g_il2cpp.domain_get_assemblies = reinterpret_cast<Il2CppAssembly** (*)(Il2CppDomain*, size_t*)>(load_symbol(game_assembly, "il2cpp_domain_get_assemblies"));
    g_il2cpp.image_get_name = reinterpret_cast<const char* (*)(void*)>(load_symbol(game_assembly, "il2cpp_image_get_name"));
    g_il2cpp.get_corlib = reinterpret_cast<void* (*)()>(load_symbol(game_assembly, "il2cpp_get_corlib"));
    g_il2cpp.type_get_name = reinterpret_cast<const char* (*)(void*)>(load_symbol(game_assembly, "il2cpp_type_get_name"));
    g_il2cpp.array_class_get = reinterpret_cast<Il2CppClass* (*)(Il2CppClass*, uint32_t)>(load_symbol(game_assembly, "il2cpp_array_class_get"));
    g_il2cpp.array_new = reinterpret_cast<Il2CppObject* (*)(Il2CppClass*, size_t)>(load_symbol(game_assembly, "il2cpp_array_new"));
    g_il2cpp.array_object_header_size = reinterpret_cast<size_t (*)()>(load_symbol(game_assembly, "il2cpp_array_object_header_size"));
    g_il2cpp.exception_get_message = reinterpret_cast<Il2CppString* (*)(Il2CppException*)>(load_symbol(game_assembly, "il2cpp_exception_get_message"));
    g_il2cpp.string_chars = reinterpret_cast<const wchar_t* (*)(Il2CppString*)>(load_symbol(game_assembly, "il2cpp_string_chars"));
    // Steam 大厅绕过(第二阶段)新增导出: 缺失不影响启动(下面单独记日志), 不进 ok 必需项。
    g_il2cpp.method_get_return_type = reinterpret_cast<void* (*)(const Il2CppMethod*)>(load_symbol(game_assembly, "il2cpp_method_get_return_type"));
    g_il2cpp.type_get_object = reinterpret_cast<Il2CppObject* (*)(const void*)>(load_symbol(game_assembly, "il2cpp_type_get_object"));
    g_il2cpp.class_from_system_type = reinterpret_cast<Il2CppClass* (*)(Il2CppObject*)>(load_symbol(game_assembly, "il2cpp_class_from_system_type"));
    g_il2cpp.object_new = reinterpret_cast<Il2CppObject* (*)(Il2CppClass*)>(load_symbol(game_assembly, "il2cpp_object_new"));
    g_il2cpp.value_box = reinterpret_cast<Il2CppObject* (*)(Il2CppClass*, void*)>(load_symbol(game_assembly, "il2cpp_value_box"));
    g_il2cpp.class_get_property_from_name = reinterpret_cast<void* (*)(Il2CppClass*, const char*)>(load_symbol(game_assembly, "il2cpp_class_get_property_from_name"));
    g_il2cpp.property_get_get_method = reinterpret_cast<Il2CppMethod* (*)(void*)>(load_symbol(game_assembly, "il2cpp_property_get_get_method"));
    g_il2cpp.object_get_class = reinterpret_cast<Il2CppClass* (*)(Il2CppObject*)>(load_symbol(game_assembly, "il2cpp_object_get_class"));
    g_il2cpp.class_get_name = reinterpret_cast<const char* (*)(Il2CppClass*)>(load_symbol(game_assembly, "il2cpp_class_get_name"));
    g_il2cpp.class_is_valuetype = reinterpret_cast<bool (*)(const Il2CppClass*)>(load_symbol(game_assembly, "il2cpp_class_is_valuetype"));
    g_il2cpp.object_unbox = reinterpret_cast<void* (*)(Il2CppObject*)>(load_symbol(game_assembly, "il2cpp_object_unbox"));
    g_il2cpp.class_get_parent = reinterpret_cast<Il2CppClass* (*)(Il2CppClass*)>(load_symbol(game_assembly, "il2cpp_class_get_parent"));
    // 阶段3: Nullable<Lobby>.get_HasValue 挂钩 + hasValue 字段诊断(可选)。
    g_il2cpp.class_get_field_from_name = reinterpret_cast<void* (*)(Il2CppClass*, const char*)>(load_symbol(game_assembly, "il2cpp_class_get_field_from_name"));
    g_il2cpp.field_get_offset = reinterpret_cast<size_t (*)(void*)>(load_symbol(game_assembly, "il2cpp_field_get_offset"));
    g_il2cpp.field_get_value = reinterpret_cast<void (*)(void*, void*, void*)>(load_symbol(game_assembly, "il2cpp_field_get_value"));
    g_il2cpp.field_get_value_object = reinterpret_cast<Il2CppObject* (*)(void*, Il2CppObject*)>(load_symbol(game_assembly, "il2cpp_field_get_value_object"));
    // 方案B: Lobby 字段枚举 + Id 字段/属性判定 + get_Id 返回结构体尺寸核对(可选)。
    g_il2cpp.class_get_fields = reinterpret_cast<void* (*)(Il2CppClass*, void**)>(load_symbol(game_assembly, "il2cpp_class_get_fields"));
    g_il2cpp.field_get_name = reinterpret_cast<const char* (*)(void*)>(load_symbol(game_assembly, "il2cpp_field_get_name"));
    g_il2cpp.class_value_size = reinterpret_cast<uint32_t (*)(Il2CppClass*, uint32_t*)>(load_symbol(game_assembly, "il2cpp_class_value_size"));
    // 阶段5: LobbyQuery.RequestAsync 兜底(读空数组长度 + 显式 GC root), 可选。
    g_il2cpp.array_length = reinterpret_cast<uint32_t (*)(Il2CppObject*)>(load_symbol(game_assembly, "il2cpp_array_length"));
    g_il2cpp.gchandle_new = reinterpret_cast<uint32_t (*)(Il2CppObject*, bool)>(load_symbol(game_assembly, "il2cpp_gchandle_new"));
    {
        // 缺失只记日志(照现有风格): 记下"哪些大厅绕过所需的导出缺失", 便于下一轮定位。
        struct { const char* n; const void* p; } extra[] = {
            { "il2cpp_method_get_return_type", (const void*)g_il2cpp.method_get_return_type },
            { "il2cpp_type_get_object",        (const void*)g_il2cpp.type_get_object },
            { "il2cpp_class_from_system_type", (const void*)g_il2cpp.class_from_system_type },
            { "il2cpp_object_new",             (const void*)g_il2cpp.object_new },
            { "il2cpp_value_box",              (const void*)g_il2cpp.value_box },
            { "il2cpp_class_get_property_from_name", (const void*)g_il2cpp.class_get_property_from_name },
            { "il2cpp_property_get_get_method",(const void*)g_il2cpp.property_get_get_method },
            { "il2cpp_object_get_class",       (const void*)g_il2cpp.object_get_class },
            { "il2cpp_class_get_name",         (const void*)g_il2cpp.class_get_name },
            { "il2cpp_class_is_valuetype",     (const void*)g_il2cpp.class_is_valuetype },
            { "il2cpp_object_unbox",           (const void*)g_il2cpp.object_unbox },
            { "il2cpp_class_get_parent",       (const void*)g_il2cpp.class_get_parent },
            { "il2cpp_class_get_field_from_name", (const void*)g_il2cpp.class_get_field_from_name },
            { "il2cpp_field_get_offset",       (const void*)g_il2cpp.field_get_offset },
            { "il2cpp_field_get_value",        (const void*)g_il2cpp.field_get_value },
            { "il2cpp_field_get_value_object", (const void*)g_il2cpp.field_get_value_object },
            { "il2cpp_class_get_fields",       (const void*)g_il2cpp.class_get_fields },
            { "il2cpp_field_get_name",         (const void*)g_il2cpp.field_get_name },
            { "il2cpp_class_value_size",       (const void*)g_il2cpp.class_value_size },
            { "il2cpp_array_length",           (const void*)g_il2cpp.array_length },
            { "il2cpp_gchandle_new",           (const void*)g_il2cpp.gchandle_new },
        };
        std::string missing;
        for (auto& e : extra) if (!e.p) { if (!missing.empty()) missing += ", "; missing += e.n; }
        if (!missing.empty())
            log_line("[loader] optional exports missing (startup unaffected; Steam lobby bypass degrades): " + missing);
    }
    // 关键导出缺失即视为失败
    bool ok = g_il2cpp.domain_get && g_il2cpp.assembly_get_image && g_il2cpp.image_get_assembly &&
              g_il2cpp.class_from_name && g_il2cpp.class_get_method_from_name && g_il2cpp.class_get_methods &&
              g_il2cpp.method_get_name && g_il2cpp.method_get_param && g_il2cpp.method_get_param_count &&
              g_il2cpp.runtime_invoke && g_il2cpp.thread_attach && g_il2cpp.domain_get_assemblies &&
              g_il2cpp.image_get_name && g_il2cpp.get_corlib && g_il2cpp.type_get_name &&
              g_il2cpp.array_class_get && g_il2cpp.array_new && g_il2cpp.array_object_header_size;
    if (ok)
    {
        // 填充安全封装用的函数表(独立于 Il2Cpp 结构, 避免布局耦合)
        g_safe_il2cpp.domain_get = reinterpret_cast<void* (*)()>(g_il2cpp.domain_get);
        g_safe_il2cpp.assembly_get_image = reinterpret_cast<void* (*)(void*)>(g_il2cpp.assembly_get_image);
        g_safe_il2cpp.image_get_assembly = reinterpret_cast<void* (*)(void*)>(g_il2cpp.image_get_assembly);
        g_safe_il2cpp.class_from_name = reinterpret_cast<void* (*)(void*, const char*, const char*)>(g_il2cpp.class_from_name);
        g_safe_il2cpp.class_get_method_from_name = reinterpret_cast<void* (*)(void*, const char*, int)>(g_il2cpp.class_get_method_from_name);
        g_safe_il2cpp.class_get_methods = reinterpret_cast<void* (*)(void*, void**)>(g_il2cpp.class_get_methods);
        g_safe_il2cpp.method_get_name = reinterpret_cast<const char* (*)(void*)>(g_il2cpp.method_get_name);
        g_safe_il2cpp.method_get_param = reinterpret_cast<void* (*)(void*, uint32_t)>(g_il2cpp.method_get_param);
        g_safe_il2cpp.method_get_param_count = reinterpret_cast<uint32_t (*)(void*)>(g_il2cpp.method_get_param_count);
        g_safe_il2cpp.runtime_invoke = reinterpret_cast<void* (*)(void*, void*, void**, void**)>(g_il2cpp.runtime_invoke);
        g_safe_il2cpp.thread_attach = reinterpret_cast<void* (*)(void*)>(g_il2cpp.thread_attach);
        g_safe_il2cpp.domain_get_assemblies = reinterpret_cast<void** (*)(void*, size_t*)>(g_il2cpp.domain_get_assemblies);
        g_safe_il2cpp.image_get_name = reinterpret_cast<const char* (*)(void*)>(g_il2cpp.image_get_name);
        g_safe_il2cpp.get_corlib = reinterpret_cast<void* (*)()>(g_il2cpp.get_corlib);
        g_safe_il2cpp.type_get_name = reinterpret_cast<const char* (*)(void*)>(g_il2cpp.type_get_name);
        g_safe_il2cpp.array_class_get = reinterpret_cast<void* (*)(void*, uint32_t)>(g_il2cpp.array_class_get);
        g_safe_il2cpp.array_new = reinterpret_cast<void* (*)(void*, size_t)>(g_il2cpp.array_new);
        g_safe_il2cpp.array_object_header_size = reinterpret_cast<size_t (*)()>(g_il2cpp.array_object_header_size);
        g_safe_il2cpp.exception_get_message = reinterpret_cast<void* (*)(void*)>(g_il2cpp.exception_get_message);
        g_safe_il2cpp.string_chars = reinterpret_cast<const wchar_t* (*)(void*)>(g_il2cpp.string_chars);
        // Steam 大厅绕过(第二阶段): 同样转成 void* 形态(结构体与 il2cpp_safe.h 解耦)
        g_safe_il2cpp.method_get_return_type = reinterpret_cast<void* (*)(void*)>(g_il2cpp.method_get_return_type);
        g_safe_il2cpp.type_get_object = reinterpret_cast<void* (*)(void*)>(g_il2cpp.type_get_object);
        g_safe_il2cpp.class_from_system_type = reinterpret_cast<void* (*)(void*)>(g_il2cpp.class_from_system_type);
        g_safe_il2cpp.object_new = reinterpret_cast<void* (*)(void*)>(g_il2cpp.object_new);
        g_safe_il2cpp.value_box = reinterpret_cast<void* (*)(void*, void*)>(g_il2cpp.value_box);
        g_safe_il2cpp.class_get_property_from_name = reinterpret_cast<void* (*)(void*, const char*)>(g_il2cpp.class_get_property_from_name);
        g_safe_il2cpp.property_get_get_method = reinterpret_cast<void* (*)(void*)>(g_il2cpp.property_get_get_method);
        g_safe_il2cpp.object_get_class = reinterpret_cast<void* (*)(void*)>(g_il2cpp.object_get_class);
        g_safe_il2cpp.class_get_name = reinterpret_cast<const char* (*)(void*)>(g_il2cpp.class_get_name);
        g_safe_il2cpp.class_is_valuetype = reinterpret_cast<bool (*)(void*)>(g_il2cpp.class_is_valuetype);
        g_safe_il2cpp.object_unbox = reinterpret_cast<void* (*)(void*)>(g_il2cpp.object_unbox);
        g_safe_il2cpp.class_get_parent = reinterpret_cast<void* (*)(void*)>(g_il2cpp.class_get_parent);
        // 阶段3: Nullable<Lobby>.get_HasValue 挂钩 + hasValue 字段诊断。
        g_safe_il2cpp.class_get_field_from_name = reinterpret_cast<void* (*)(void*, const char*)>(g_il2cpp.class_get_field_from_name);
        g_safe_il2cpp.field_get_offset = reinterpret_cast<size_t (*)(void*)>(g_il2cpp.field_get_offset);
        g_safe_il2cpp.field_get_value = reinterpret_cast<void (*)(void*, void*, void*)>(g_il2cpp.field_get_value);
        g_safe_il2cpp.field_get_value_object = reinterpret_cast<void* (*)(void*, void*)>(g_il2cpp.field_get_value_object);
        // 方案B: Lobby 字段枚举 + Id 字段/属性判定 + get_Id 返回结构体尺寸核对。
        g_safe_il2cpp.class_get_fields = reinterpret_cast<void* (*)(void*, void**)>(g_il2cpp.class_get_fields);
        g_safe_il2cpp.field_get_name = reinterpret_cast<const char* (*)(void*)>(g_il2cpp.field_get_name);
        g_safe_il2cpp.class_value_size = reinterpret_cast<uint32_t (*)(void*, uint32_t*)>(g_il2cpp.class_value_size);
        g_safe_il2cpp.array_length = reinterpret_cast<uint32_t (*)(void*)>(g_il2cpp.array_length);
        g_safe_il2cpp.gchandle_new = reinterpret_cast<uint32_t (*)(void*, bool)>(g_il2cpp.gchandle_new);
    }
    return ok;
}

// ---------- HybridCLR 就绪检测 ----------

/// 诊断: 把域内程序集清单落盘到 logs\aot-assemblies.txt。
///
/// 用途: 游戏的 BCL 是**被裁剪过的**(IL2CPP 托管裁剪), 热更代码(含第三方程序集)只能引用
/// 这里存在的程序集与类型。一旦看到 "TypeLoadException: Could not load type 'X' from
/// assembly 'Y'", 第一件事就是对照这份清单与 Y 的类型表 —— 而不是去猜。
static void dump_aot_assemblies(const std::vector<std::string>& names)
{
    try
    {
        std::wstring dir = logs_dir();
        if (dir.empty()) return;
        fs::create_directories(dir);
        std::ofstream out(fs::path(dir) / L"aot-assemblies.txt",
                          std::ios::binary | std::ios::trunc);
        if (!out) return;
        out << "# AOT assemblies in the game domain (enumerated when HybridCLR is ready, total " << names.size() << ")\n";
        out << "# Hot-update code and third-party assemblies may only reference assemblies/types listed here\n";
        std::vector<std::string> sorted = names;
        std::sort(sorted.begin(), sorted.end());
        for (auto& n : sorted) out << n << "\n";
    }
    catch (...) { /* 诊断失败不影响加载 */ }
}

static bool hybridclr_ready(Il2CppDomain* domain)
{
    // 安全封装: 枚举域内程序集(空检查 + 错误缓冲)
    auto names = cesium_safe::safe_list_assembly_names(g_safe_il2cpp);
    for (auto& n : names)
    {
        if (n.find("AstralParty.Runtime") != std::string::npos)
        {
            // 只在第一次就绪时落盘一次
            static bool dumped = false;
            if (!dumped) { dumped = true; dump_aot_assemblies(names); }
            return true;
        }
    }
    return false;
}

bool wait_hybridclr(Il2CppDomain* domain, DWORD timeout_secs)
{
    ULONGLONG end = GetTickCount64() + (ULONGLONG)timeout_secs * 1000;
    ULONGLONG last_diag = 0;
    for (;;)
    {
        if (hybridclr_ready(domain)) return true;
        if (GetTickCount64() >= end) return false;
        // 诊断: 若安全封装枚举报错, 每 5s 打印一次错误(正常枚举不刷屏)
        if (!cesium_safe::g_last_error.empty() && GetTickCount64() - last_diag >= 5000)
        {
            last_diag = GetTickCount64();
            log_line("[hybridclr] enumeration error: " + cesium_safe::g_last_error);
        }
        Sleep(300);
    }
}

// ---------- Assembly.Load(byte[]) ----------

// 在 System.Reflection.Assembly 上找 Load(byte[]) 重载(遍历方法,按参数类型筛选)。
//
// 每个程序集(1 个 SDK + N 个 mod)都会走一次这段扫描。旧实现每遇到一个候选重载就打
// 两行日志, 十个程序集就是 30 行、占整份日志的四分之一 —— 而结论永远只有一句
// ("有 3 个重载, 选 byte[]")。这里改成**整个进程只报一次结论**, 且合并成一行;
// 只有找不到 byte[] 重载时才把候选清单打出来(那才是需要人看的失败现场)。
static Il2CppMethod* find_load_bytearray(Il2CppClass* cls)
{
    std::vector<std::string> candidates;
    Il2CppMethod* chosen = nullptr;

    void* iter = nullptr;
    for (;;)
    {
        Il2CppMethod* m = g_il2cpp.class_get_methods(cls, &iter);
        if (!m) break;
        const char* n = g_il2cpp.method_get_name(m);
        if (!n || strcmp(n, "Load") != 0) continue;
        if (g_il2cpp.method_get_param_count(m) != 1) continue;
        void* param = g_il2cpp.method_get_param(m, 0);
        if (!param) continue;
        const char* tn = g_il2cpp.type_get_name(param);
        if (!tn) continue;
        candidates.emplace_back(tn);
        if (!chosen && strcmp(tn, "System.Byte[]") == 0) chosen = m;
    }

    static bool reported = false;
    if (!reported)
    {
        std::string list;
        for (auto& c : candidates) list += (list.empty() ? "" : ", ") + c;
        if (chosen)
            log_line("[loader] Assembly.Load overloads: " + list + " -> using byte[]");
        else
            log_line("[loader] Assembly.Load(byte[]) not found, candidates: " + (list.empty() ? std::string("(none)") : list));
        reported = true;
    }
    return chosen;
}

// 枚举域内所有程序集的 image 名称,用于对比 Load 前后新增的程序集。
static std::vector<std::string> list_assembly_names(Il2CppDomain* domain)
{
    std::vector<std::string> names;
    size_t count = 0;
    Il2CppAssembly** p = g_il2cpp.domain_get_assemblies(domain, &count);
    if (!p) return names;
    for (size_t i = 0; i < count; i++)
    {
        Il2CppAssembly* asm_ = p[i];
        if (!asm_) continue;
        void* image = g_il2cpp.assembly_get_image(asm_);
        if (!image) continue;
        const char* n = g_il2cpp.image_get_name(image);
        if (!n) continue;
        names.emplace_back(n);
    }
    return names;
}

// 调用 System.Reflection.Assembly.Load(byte[]) 并返回加载后的程序集指针。
// 返回 nullptr 表示失败(原因写入 err_msg)。
Il2CppAssembly* load_assembly_bytes(Il2CppDomain* domain, const std::vector<uint8_t>& bytes, std::string& err_msg)
{
    err_msg.clear();
    void* corlib = g_il2cpp.get_corlib();
    if (!corlib) { err_msg = "corlib null"; return nullptr; }
    Il2CppClass* ac = g_il2cpp.class_from_name(corlib, "System.Reflection", "Assembly");
    if (!ac) { err_msg = "Assembly class null"; return nullptr; }

    Il2CppClass* bc = g_il2cpp.class_from_name(corlib, "System", "Byte");
    if (!bc) { err_msg = "Byte class null"; return nullptr; }

    Il2CppClass* bac = g_il2cpp.array_class_get(bc, 1);
    Il2CppObject* arr = g_il2cpp.array_new(bac, bytes.size());
    if (!arr) { err_msg = "byte[] null"; return nullptr; }

    // x64 IL2CPP 数组布局:Il2CppObject(16) + bounds(8, SZARRAY 为 null) + max_length(8) = 数据起点 32。
    // 若导出的 array_object_header_size 正好是数据起点则直接用,否则回退 32。
    size_t reported = g_il2cpp.array_object_header_size();
    size_t data_offset = (reported >= 32) ? reported : 32;
    memcpy(reinterpret_cast<uint8_t*>(arr) + data_offset, bytes.data(), bytes.size());

    Il2CppMethod* load = find_load_bytearray(ac);
    if (!load) { err_msg = "Assembly.Load(byte[]) not found"; return nullptr; }

    void* arg = arr;
    std::vector<std::string> before = list_assembly_names(domain);

    // 用 safe_invoke_static 而不是直接 runtime_invoke: 它会把**托管异常的类型与消息**转成
    // UTF-8 写进 g_last_error。原实现只留下 "Assembly.Load threw exception", 异常内容被丢弃 ——
    // 一旦第三方程序集加载失败(例如它引用了被 IL2CPP 裁剪掉的程序集), 就完全没有线索。
    cesium_safe::g_last_error.clear();
    Il2CppObject* asm_obj = reinterpret_cast<Il2CppObject*>(
        cesium_safe::safe_invoke_static(g_safe_il2cpp, load, nullptr, &arg, "Assembly.Load"));
    if (!asm_obj)
    {
        err_msg = cesium_safe::g_last_error.empty() ? "Assembly.Load returned null" : cesium_safe::g_last_error;
        return nullptr;
    }
    std::vector<std::string> after = list_assembly_names(domain);

    // 对比 Load 前后,找新增的程序集(不依赖反射对象布局)。
    int new_idx = -1;
    for (size_t i = 0; i < after.size(); i++)
    {
        bool existed = false;
        for (auto& b : before) if (b == after[i]) { existed = true; break; }
        if (!existed) { new_idx = (int)i; break; }
    }
    if (new_idx < 0) { err_msg = "no new assembly appeared after load"; return nullptr; }

    size_t count = 0;
    Il2CppAssembly** p = g_il2cpp.domain_get_assemblies(domain, &count);
    if (!p) { err_msg = "domain assemblies null"; return nullptr; }
    return p[new_idx];
}

// 调用入口 {entry_type}.{entry_method}。
bool run_entry(Il2CppAssembly* asm_, const char* entry_type, const char* entry_method, std::string& err_msg)
{
    err_msg.clear();
    void* image = g_il2cpp.assembly_get_image(asm_);
    if (!image) { err_msg = "mod image null"; return false; }

    // 拆分命名空间与类名(最后一个 '.' 分隔)
    std::string type(entry_type);
    std::string ns, cn;
    auto pos = type.find_last_of('.');
    if (pos == std::string::npos) { ns = ""; cn = type; }
    else { ns = type.substr(0, pos); cn = type.substr(pos + 1); }

    Il2CppClass* cls = g_il2cpp.class_from_name(image, ns.c_str(), cn.c_str());
    if (!cls) { err_msg = "entry class not found: " + type; return false; }
    Il2CppMethod* method = g_il2cpp.class_get_method_from_name(cls, entry_method, 0);
    if (!method) { err_msg = "entry method not found: " + std::string(entry_method); return false; }

    // 安全封装: runtime_invoke 带空检查 + 异常转译(错误在 g_last_error)
    cesium_safe::g_last_error.clear();
    void* r = cesium_safe::safe_invoke_static(g_safe_il2cpp, method, nullptr, nullptr, "run_entry");
    if (cesium_safe::g_last_error.empty())
    {
        return true;
    }
    err_msg = cesium_safe::g_last_error;
    return false;
}

// ---------- 引导线程直接使用的入口 ----------

Il2CppDomain* domain_get()
{
    return g_il2cpp.domain_get();
}

void thread_attach(Il2CppDomain* domain)
{
    g_il2cpp.thread_attach(domain);
}

const cesium_safe::il2cpp_tbl& safe_il2cpp()
{
    return g_safe_il2cpp;
}

} // namespace cesium::runtime::boot

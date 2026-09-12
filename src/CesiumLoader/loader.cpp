// loader.cpp - IL2CPP 桥 + 引导线程 + Assembly.Load(byte[]) + mod 加载
//
// 对应 Rust 版 lib.rs 的 IL2CPP 桥与 boot_thread:
//   1. 等待 GameAssembly.dll 加载(60s 超时)
//   2. 解析 il2cpp_* 导出函数
//   3. 等待 il2cpp domain 就绪(30s) + thread_attach
//   4. 等待 HybridCLR 热更(AstralParty.Runtime 出现,60s)
//   5. 设置环境变量 CESIUM_MODS_DIR / CESIUM_LOG_DIR / CESIUM_SDK_DIR
//   6. 先 Assembly.Load sdk\*.dll(不调入口), 再加载 mods\*.dll 并调 {Name}.ModEntry.Main
//   7. 启动 activity-mod.log -> 控制台 转发线程

#include "loader.h"

#include <tlhelp32.h>
#include <vector>
#include <filesystem>
#include <fstream>
#include <cstring>

namespace fs = std::filesystem;

// ---------- IL2CPP 导出函数类型 ----------

using Il2CppDomain = void;
using Il2CppAssembly = void;
using Il2CppClass = void;
using Il2CppMethod = void;
using Il2CppObject = void;
using Il2CppException = void;
using Il2CppThread = void;

struct Il2Cpp
{
    Il2CppDomain* (*domain_get)() = nullptr;
    void* (*assembly_get_image)(Il2CppAssembly*) = nullptr;
    Il2CppAssembly* (*image_get_assembly)(void*) = nullptr;
    Il2CppClass* (*class_from_name)(void*, const char*, const char*) = nullptr;
    Il2CppMethod* (*class_get_method_from_name)(Il2CppClass*, const char*, int) = nullptr;
    Il2CppMethod* (*class_get_methods)(Il2CppClass*, void**) = nullptr;
    const char* (*method_get_name)(Il2CppMethod*) = nullptr;
    void* (*method_get_param)(Il2CppMethod*, uint32_t) = nullptr;
    uint32_t (*method_get_param_count)(Il2CppMethod*) = nullptr;
    Il2CppObject* (*runtime_invoke)(Il2CppMethod*, Il2CppObject*, void**, Il2CppException**) = nullptr;
    Il2CppThread* (*thread_attach)(Il2CppDomain*) = nullptr;
    Il2CppAssembly** (*domain_get_assemblies)(Il2CppDomain*, size_t*) = nullptr;
    const char* (*image_get_name)(void*) = nullptr;
    void* (*get_corlib)() = nullptr;
    const char* (*type_get_name)(void*) = nullptr;
    Il2CppClass* (*array_class_get)(Il2CppClass*, uint32_t) = nullptr;
    Il2CppObject* (*array_new)(Il2CppClass*, size_t) = nullptr;
    size_t (*array_object_header_size)() = nullptr;
};

// ---------- 工具 ----------

static void* load_symbol(HMODULE module, const char* name)
{
    return reinterpret_cast<void*>(GetProcAddress(module, name));
}

static Il2Cpp g_il2cpp;

static bool get_il2cpp(HMODULE game_assembly)
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
    // 关键导出缺失即视为失败
    return g_il2cpp.domain_get && g_il2cpp.assembly_get_image && g_il2cpp.image_get_assembly &&
           g_il2cpp.class_from_name && g_il2cpp.class_get_method_from_name && g_il2cpp.class_get_methods &&
           g_il2cpp.method_get_name && g_il2cpp.method_get_param && g_il2cpp.method_get_param_count &&
           g_il2cpp.runtime_invoke && g_il2cpp.thread_attach && g_il2cpp.domain_get_assemblies &&
           g_il2cpp.image_get_name && g_il2cpp.get_corlib && g_il2cpp.type_get_name &&
           g_il2cpp.array_class_get && g_il2cpp.array_new && g_il2cpp.array_object_header_size;
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

static HMODULE wait_module(const wchar_t* name, DWORD timeout_secs)
{
    ULONGLONG end = GetTickCount64() + (ULONGLONG)timeout_secs * 1000;
    for (;;)
    {
        HMODULE m = find_module(name);
        if (m) return m;
        if (GetTickCount64() >= end) return nullptr;
        Sleep(200);
    }
}

// ---------- HybridCLR 就绪检测 ----------

static bool hybridclr_ready(Il2CppDomain* domain)
{
    size_t count = 0;
    Il2CppAssembly** p = g_il2cpp.domain_get_assemblies(domain, &count);
    if (!p) return false;
    for (size_t i = 0; i < count; i++)
    {
        Il2CppAssembly* asm_ = p[i];
        if (!asm_) continue;
        void* image = g_il2cpp.assembly_get_image(asm_);
        if (!image) continue;
        const char* n = g_il2cpp.image_get_name(image);
        if (!n) continue;
        if (strstr(n, "AstralParty.Runtime")) return true;
    }
    return false;
}

static bool wait_hybridclr(Il2CppDomain* domain, DWORD timeout_secs)
{
    ULONGLONG end = GetTickCount64() + (ULONGLONG)timeout_secs * 1000;
    for (;;)
    {
        if (hybridclr_ready(domain)) return true;
        if (GetTickCount64() >= end) return false;
        Sleep(300);
    }
}

// ---------- Assembly.Load(byte[]) ----------

// 在 System.Reflection.Assembly 上找 Load(byte[]) 重载(遍历方法,按参数类型筛选)。
static Il2CppMethod* find_load_bytearray(Il2CppClass* cls)
{
    void* iter = nullptr;
    for (;;)
    {
        Il2CppMethod* m = g_il2cpp.class_get_methods(cls, &iter);
        if (!m) return nullptr;
        const char* n = g_il2cpp.method_get_name(m);
        if (!n || strcmp(n, "Load") != 0) continue;
        if (g_il2cpp.method_get_param_count(m) != 1) continue;
        void* param = g_il2cpp.method_get_param(m, 0);
        if (!param) continue;
        const char* tn = g_il2cpp.type_get_name(param);
        if (!tn) continue;
        log_line("[hijack] Assembly.Load candidate param: ");
        log_line(tn);
        if (strcmp(tn, "System.Byte[]") == 0) return m;
    }
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
static Il2CppAssembly* load_assembly_bytes(Il2CppDomain* domain, const std::vector<uint8_t>& bytes, std::string& err_msg)
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
    Il2CppException* exc = nullptr;
    std::vector<std::string> before = list_assembly_names(domain);
    Il2CppObject* asm_obj = g_il2cpp.runtime_invoke(load, nullptr, &arg, &exc);
    if (exc) { err_msg = "Assembly.Load threw exception"; return nullptr; }
    if (!asm_obj) { err_msg = "Assembly.Load returned null"; return nullptr; }
    std::vector<std::string> after = list_assembly_names(domain);

    // 对比 Load 前后,找新增的程序集(不依赖反射对象布局)。
    int new_idx = -1;
    for (size_t i = 0; i < after.size(); i++)
    {
        bool existed = false;
        for (auto& b : before) if (b == after[i]) { existed = true; break; }
        if (!existed) { new_idx = (int)i; break; }
    }
    if (new_idx < 0) { err_msg = "未发现新增程序集"; return nullptr; }

    size_t count = 0;
    Il2CppAssembly** p = g_il2cpp.domain_get_assemblies(domain, &count);
    if (!p) { err_msg = "domain assemblies null"; return nullptr; }
    return p[new_idx];
}

// 调用入口 {entry_type}.{entry_method}。
static bool run_entry(Il2CppAssembly* asm_, const char* entry_type, const char* entry_method, std::string& err_msg)
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

    Il2CppException* exc = nullptr;
    g_il2cpp.runtime_invoke(method, nullptr, nullptr, &exc);
    if (exc) { err_msg = "entry threw exception"; return false; }
    return true;
}

// ---------- 日志转发线程(activity-mod.log -> 控制台) ----------

// mod 无法直接 P/Invoke 到自定义导出(IL2CPP 限制), 改为写文件,
// 本线程监控 activity-mod.log, 把新增行写到控制台窗口。
static DWORD WINAPI forward_activity_log(LPVOID param)
{
    std::wstring log_dir = *reinterpret_cast<std::wstring*>(param);
    delete reinterpret_cast<std::wstring*>(param);

    std::wstring path = log_dir + L"\\activity-mod.log";
    LARGE_INTEGER last_len{};
    last_len.QuadPart = 0;
    // 初始:跳过已有内容(只转发"从现在起"的新日志)
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad))
    {
        LARGE_INTEGER size;
        size.HighPart = fad.nFileSizeHigh;
        size.LowPart = fad.nFileSizeLow;
        last_len = size;
    }

    for (;;)
    {
        Sleep(150);
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) continue;
        LARGE_INTEGER cur;
        cur.HighPart = fad.nFileSizeHigh;
        cur.LowPart = fad.nFileSizeLow;
        if (cur.QuadPart <= last_len.QuadPart) continue;

        HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) continue;
        LARGE_INTEGER offset = last_len;
        SetFilePointerEx(f, offset, nullptr, FILE_BEGIN);
        std::vector<char> buf((size_t)(cur.QuadPart - last_len.QuadPart));
        DWORD read = 0;
        if (ReadFile(f, buf.data(), (DWORD)buf.size(), &read, nullptr))
        {
            // 按行转发
            std::string text(buf.data(), read);
            size_t start = 0;
            while (start < text.size())
            {
                size_t end = text.find('\n', start);
                std::string line = (end == std::string::npos)
                    ? text.substr(start) : text.substr(start, end - start);
                // 去 \r
                while (!line.empty() && (line.back() == '\r')) line.pop_back();
                // 去首尾空白
                size_t b = line.find_first_not_of(" \t\r\n");
                if (b != std::string::npos)
                {
                    console_write(line.c_str() + b);
                }
                if (end == std::string::npos) break;
                start = end + 1;
            }
        }
        CloseHandle(f);
        last_len = cur;
    }
    return 0;
}

// ---------- 引导线程 ----------

static void set_env_w(const wchar_t* name, const std::wstring& value)
{
    SetEnvironmentVariableW(name, value.c_str());
}

static DWORD WINAPI boot_thread(LPVOID)
{
    ULONGLONG started = GetTickCount64();
    // 控制台在引导线程(非 loader lock)里初始化,避免在 DllMain 环境分配窗口。
    console_init();
    log_line("[hijack] DllMain 引导线程启动");

    HMODULE ga = wait_module(L"GameAssembly.dll", 60);
    if (!ga) { log_line("[hijack] GameAssembly.dll 超时(60s)"); return 0; }
    log_line("[hijack] GameAssembly.dll 已加载");

    if (!get_il2cpp(ga)) { log_line("[hijack] il2cpp 导出解析失败"); return 0; }

    Il2CppDomain* domain = nullptr;
    for (;;)
    {
        domain = g_il2cpp.domain_get();
        if (domain) break;
        if (GetTickCount64() - started > 30000) { log_line("[hijack] il2cpp domain 超时(30s)"); return 0; }
        Sleep(200);
    }
    g_il2cpp.thread_attach(domain);
    log_line("[hijack] il2cpp 运行时就绪, 等待 HybridCLR 热更...");

    if (!wait_hybridclr(domain, 60)) { log_line("[hijack] AstralParty.Runtime 未在 60s 内出现"); return 0; }
    log_line("[hijack] HybridCLR 热更就绪");

    // 读取 mod DLL:扫描 mods 目录下所有 .dll,逐个尝试 Assembly.Load + 入口调用。
    // 设置环境变量供 mod 读取(日志目录等)。
    std::wstring mods = mods_dir();
    std::wstring logs = logs_dir();
    std::wstring sdk = sdk_dir();
    set_env_w(L"CESIUM_MODS_DIR", mods);
    set_env_w(L"CESIUM_LOG_DIR", logs);
    set_env_w(L"CESIUM_SDK_DIR", sdk);
    log_line(L"[hijack] 加载器根目录: " + loader_root());
    log_line(L"[hijack] SDK 目录: " + sdk);
    log_line(L"[hijack] mods 目录: " + mods);
    log_line(L"[hijack] 日志目录: " + logs);

    // 先加载 SDK 依赖(不调入口): 供 mods 引用。顺序: 按文件名排序, 保证确定性。
    if (fs::exists(sdk))
    {
        std::vector<fs::path> sdk_dlls;
        for (auto& entry : fs::directory_iterator(sdk))
        {
            if (entry.is_regular_file() && _stricmp(entry.path().extension().string().c_str(), ".dll") == 0)
                sdk_dlls.push_back(entry.path());
        }
        std::sort(sdk_dlls.begin(), sdk_dlls.end());
        for (auto& dll : sdk_dlls)
        {
            std::string name = dll.stem().string();
            std::ifstream in(dll, std::ios::binary);
            if (!in) { log_line("[hijack] SDK " + name + " 读取失败"); continue; }
            std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
            std::string err;
            if (load_assembly_bytes(domain, bytes, err))
                log_line("[hijack] SDK " + name + " 加载成功");
            else
                log_line("[hijack] SDK " + name + " 加载失败: " + err);
        }
    }

    if (!fs::exists(mods))
    {
        log_line(L"[hijack] 读取 mods 目录失败(不存在): " + mods);
        return 0;
    }
    std::vector<fs::path> dlls;
    for (auto& entry : fs::directory_iterator(mods))
    {
        if (entry.is_regular_file() && _stricmp(entry.path().extension().string().c_str(), ".dll") == 0)
            dlls.push_back(entry.path());
    }
    std::sort(dlls.begin(), dlls.end());
    log_line("[hijack] 发现 " + std::to_string(dlls.size()) + " 个 DLL");

    for (auto& dll : dlls)
    {
        std::string name = dll.stem().string();
        std::ifstream in(dll, std::ios::binary);
        if (!in) { log_line("[hijack] 读取 " + name + " 失败"); continue; }
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        log_line("[hijack] 尝试加载 " + name + ": " + std::to_string(bytes.size()) + " bytes");

        std::string err;
        Il2CppAssembly* asm_ = load_assembly_bytes(domain, bytes, err);
        if (!asm_) { log_line("[hijack] " + name + " Assembly.Load 失败: " + err); continue; }
        log_line("[hijack] " + name + " Assembly.Load(byte[]) 成功");

        std::string entry_type = name + ".ModEntry";
        if (run_entry(asm_, entry_type.c_str(), "Main", err))
            log_line("[hijack] " + name + " 入口执行成功");
        else
            log_line("[hijack] " + name + " 入口失败: " + err);
    }
    log_line("[hijack] 引导线程结束");

    // 启动"mod 日志 -> 控制台"转发线程(持续运行直到进程退出)
    auto* log_dir_ptr = new std::wstring(logs);
    HANDLE h = CreateThread(nullptr, 0, forward_activity_log, log_dir_ptr, 0, nullptr);
    if (h) CloseHandle(h);
    return 0;
}

// ---------- 启动入口(由 exports.cpp 的 maybe_start_boot 调用) ----------

bool boot_il2cpp_and_load_mods()
{
    HANDLE h = CreateThread(nullptr, 0, boot_thread, nullptr, 0, nullptr);
    if (!h) return false;
    CloseHandle(h);
    return true;
}

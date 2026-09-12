// loader.cpp - IL2CPP 桥 + 引导线程 (Doorstop 式薄引导)
//
// 原生层只负责:
//   1. 读取 doorstop_config.json(enabled 开关 / 超时 / 控制台)
//   2. 等待 GameAssembly.dll 加载(超时)
//   3. 解析 il2cpp_* 导出函数
//   4. 等待 il2cpp domain 就绪 + thread_attach
//   5. 等待 HybridCLR 热更(AstralParty.Runtime 出现)
//   6. 设置环境变量 CESIUM_* 目录
//   7. 通过 Assembly.Load(byte[]) 加载托管引导程序 (CesiumLoader.Bootstrap.dll)
//      并调用其入口 —— SDK 加载 / mod 枚举 / 入口调用 全部在托管层完成
//   8. 启动 activity-mod.log -> 控制台 转发线程
//
// 这样 native 保持薄引导, mod 编排逻辑在 C# 里(可脱离游戏单元测试)。

#include "loader.h"

#include "config.h"

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
using Il2CppString = void;

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
    Il2CppString* (*exception_get_message)(Il2CppException*) = nullptr;
    const wchar_t* (*string_chars)(Il2CppString*) = nullptr;
};

// ---------- 工具 ----------

static void* load_symbol(HMODULE module, const char* name)
{
    return reinterpret_cast<void*>(GetProcAddress(module, name));
}

// 宽字符 → UTF-8
static std::string utf8_from_wide(const wchar_t* w)
{
    if (!w) return "";
    int len = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return "";
    std::string s(len - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), len, nullptr, nullptr);
    return s;
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
    g_il2cpp.exception_get_message = reinterpret_cast<Il2CppString* (*)(Il2CppException*)>(load_symbol(game_assembly, "il2cpp_exception_get_message"));
    g_il2cpp.string_chars = reinterpret_cast<const wchar_t* (*)(Il2CppString*)>(load_symbol(game_assembly, "il2cpp_string_chars"));
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
    if (exc)
    {
        // 取异常消息(ClassName: Message)，方便定位 mod 入口失败原因
        if (g_il2cpp.exception_get_message && g_il2cpp.string_chars)
        {
            Il2CppString* msg = g_il2cpp.exception_get_message(exc);
            if (msg)
            {
                const wchar_t* w = g_il2cpp.string_chars(msg);
                if (w) { err_msg = "entry threw exception: " + utf8_from_wide(w); return false; }
            }
        }
        err_msg = "entry threw exception (no message)";
        return false;
    }
    return true;
}

// ---------- 日志转发线程(activity-mod.log -> 控制台) ----------

// mod / bootstrap 无法直接 P/Invoke 到自定义导出(IL2CPP 限制), 改为写文件,
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
    // 若由 DllMain(DLL_PROCESS_ATTACH) 启动, 先等 loader lock 释放:
    // 进程初始化期间所有 DLL 的 DllMain 通常在几百 ms 内完成, 睡 1500ms 足够避开。
    // 若由首次转发调用触发(进程早已初始化), 这一觉无副作用。
    Sleep(1500);

    ULONGLONG started = GetTickCount64();

    // 1. 读取 Doorstop 式配置(缺失/损坏时全默认)
    LoaderConfig cfg = load_config(config_path());

    // 2. 控制台在引导线程(非 loader lock)里初始化,避免在 DllMain 环境分配窗口
    if (cfg.consoleEnabled)
    {
        console_init(cfg.consoleTopmost);
    }
    log_line("[hijack] version.dll Doorstop 引导线程启动 (enabled=" + std::string(cfg.enabled ? "true" : "false") + ")");

    // 3. 总开关: disabled 时静默退出, 游戏原样运行
    if (!cfg.enabled)
    {
        log_line("[hijack] doorstop_config.json enabled=false, 跳过引导");
        return 0;
    }

    // 4. 等待 GameAssembly.dll
    HMODULE ga = wait_module(L"GameAssembly.dll", cfg.gameAssemblyTimeoutSec);
    if (!ga) { log_line("[hijack] GameAssembly.dll 超时"); return 0; }
    log_line("[hijack] GameAssembly.dll 已加载");

    if (!get_il2cpp(ga)) { log_line("[hijack] il2cpp 导出解析失败"); return 0; }

    Il2CppDomain* domain = nullptr;
    for (;;)
    {
        domain = g_il2cpp.domain_get();
        if (domain) break;
        if (GetTickCount64() - started > (ULONGLONG)cfg.domainTimeoutSec * 1000) { log_line("[hijack] il2cpp domain 超时"); return 0; }
        Sleep(200);
    }
    g_il2cpp.thread_attach(domain);
    log_line("[hijack] il2cpp 运行时就绪, 等待 HybridCLR 热更...");

    if (!wait_hybridclr(domain, cfg.hybridclrTimeoutSec)) { log_line("[hijack] AstralParty.Runtime 超时"); return 0; }
    log_line("[hijack] HybridCLR 热更就绪");

    // 5. 设置环境变量, 供托管引导程序与 mod 读取
    std::wstring mods = mods_dir();
    std::wstring logs = logs_dir();
    std::wstring sdk = sdk_dir();
    std::wstring boot = bootstrap_dir();
    set_env_w(L"CESIUM_MODS_DIR", mods);
    set_env_w(L"CESIUM_LOG_DIR", logs);
    set_env_w(L"CESIUM_SDK_DIR", sdk);
    set_env_w(L"CESIUM_BOOTSTRAP_DIR", boot);
    log_line(L"[hijack] 加载器根目录: " + loader_root());
    log_line(L"[hijack] bootstrap 目录: " + boot);
    log_line(L"[hijack] SDK 目录: " + sdk);
    log_line(L"[hijack] mods 目录: " + mods);
    log_line(L"[hijack] 日志目录: " + logs);

    // 6a. 实验特性: useManagedBootstrap=true 时, 加载 bootstrap DLL 并调用入口,
    //     由托管代码负责 sdk/mods 加载与入口调用(注意 HybridCLR 反射裁剪风险)。
    //     默认 false: 走下面 6b 的原生编排(可靠路径)。
    if (cfg.useManagedBootstrap)
    {
        fs::path bootstrap_path = fs::path(boot) / fs::path(cfg.bootstrapAssembly);
        if (!fs::exists(bootstrap_path))
        {
            log_line("[hijack] bootstrap 程序集不存在: " + cfg.bootstrapAssembly);
            return 0;
        }
        std::ifstream in(bootstrap_path, std::ios::binary);
        if (!in) { log_line("[hijack] bootstrap 读取失败: " + cfg.bootstrapAssembly); return 0; }
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        log_line("[hijack] 尝试加载 bootstrap: " + cfg.bootstrapAssembly + " (" + std::to_string(bytes.size()) + " bytes)");

        std::string err;
        Il2CppAssembly* asm_ = load_assembly_bytes(domain, bytes, err);
        if (!asm_) { log_line("[hijack] bootstrap Assembly.Load 失败: " + err); return 0; }
        log_line("[hijack] bootstrap Assembly.Load(byte[]) 成功");

        if (!run_entry(asm_, cfg.bootstrapType.c_str(), cfg.bootstrapMethod.c_str(), err))
        {
            log_line("[hijack] bootstrap 入口失败: " + err);
            return 0;
        }
        log_line("[hijack] bootstrap 入口执行成功, 引导线程结束");

        // 启动"mod 日志 -> 控制台"转发线程(持续运行直到进程退出)
        if (cfg.forwardActivityLog)
        {
            auto* log_dir_ptr = new std::wstring(logs);
            HANDLE h = CreateThread(nullptr, 0, forward_activity_log, log_dir_ptr, 0, nullptr);
            if (h) CloseHandle(h);
        }
        return 0;
    }

    // 6b. 原生编排(默认): 加载 sdk 依赖, 枚举 mods, 逐个 Assembly.Load + 调用入口。
    //     全部走 il2cpp 原生 API, 不受 HybridCLR AOT 反射裁剪影响 —— 已验证可靠。

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

    // 7. 启动"mod 日志 -> 控制台"转发线程(持续运行直到进程退出)
    if (cfg.forwardActivityLog)
    {
        auto* log_dir_ptr = new std::wstring(logs);
        HANDLE h = CreateThread(nullptr, 0, forward_activity_log, log_dir_ptr, 0, nullptr);
        if (h) CloseHandle(h);
    }
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

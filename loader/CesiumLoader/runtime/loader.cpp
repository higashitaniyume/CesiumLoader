// loader.cpp - 引导线程 (Doorstop 式薄引导)
//
// 原生层只负责:
//   1. 读取 doorstop_config.json(enabled 开关 / 超时 / 控制台)
//   2. 等待 GameAssembly.dll 加载(超时)
//   3. 解析 il2cpp_* 导出函数                                   -> runtime/il2cpp_boot.cpp
//   4. 等待 il2cpp domain 就绪 + thread_attach                  -> 本文件(调用 il2cpp_boot)
//   5. 等待 HybridCLR 热更(AstralParty.Runtime 出现)            -> runtime/il2cpp_boot.cpp
//   6. 设置环境变量 CESIUM_* 目录 + 本次启动的日志文件路径(每次启动一套)
//   7. 通过 Assembly.Load(byte[]) 加载托管引导程序 (CesiumLoader.Bootstrap.dll)
//      并调用其入口 —— SDK 加载 / mod 枚举 / 入口调用 全部在托管层完成(本文件编排,
//      Assembly.Load/入口调用本身在 runtime/il2cpp_boot.cpp)
//   8. 启动 activity-mod-<会话>.log -> 控制台 转发线程          -> runtime/activity_forward.cpp
//
// 这样 native 保持薄引导, mod 编排逻辑在 C# 里(可脱离游戏单元测试)。
// 内部接口(IL2CPP 函数表 / 转发线程)见 runtime/loader_internal.h; 对外仍是 platform/loader.h。

#include "platform/loader.h"

#include "config/config.h"
#include "speed/speedhack.h"
#include "speed/speedctl.h"
#include "window/windowctl.h"
#include "mods/modmeta.h"
#include "logging/logging.h"
#include "runtime/loader_internal.h"
#include "runtime/state.h"
#include "shell/shell.h"
#include "steam/steamhack.h"

#include <tlhelp32.h>
#include <vector>
#include <filesystem>
#include <fstream>
#include <cstring>
#include <cstdio>
#include <string>
#include <map>

namespace fs = std::filesystem;

namespace boot = cesium::runtime::boot;
namespace forward = cesium::runtime::forward;

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

// 耗时的展示文本: "12 ms" / "0.9s"(ms 级给整数, 秒级给一位小数)
static std::string fmt_ms(ULONGLONG ms)
{
    char buf[32] = {};
    std::snprintf(buf, sizeof buf, "%llu ms", static_cast<unsigned long long>(ms));
    return buf;
}

static std::string fmt_seconds(ULONGLONG ms)
{
    char buf[32] = {};
    std::snprintf(buf, sizeof buf, "%.1fs", static_cast<double>(ms) / 1000.0);
    return buf;
}

// ---------- 展示辅助: 路径 / 版本 ----------

static std::wstring parent_dir(const std::wstring& path)
{
    const size_t pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? std::wstring() : path.substr(0, pos);
}

static std::wstring file_name_of(const std::wstring& path)
{
    const size_t pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? path : path.substr(pos + 1);
}

// ---------- 引导线程 ----------

// 故障体验: 把 mod 加载/入口失败写入本次启动的 logs\mod-errors-<会话>.log(与 SDK ReportCrash 同一文件)。
// 即使 mod 自身崩溃抛异常, 这里也能记录"哪个 mod 失败 + 原因", 方便定位。
// 具体写盘由 spdlog 负责(见 logging.cpp): 行格式 [时间] [mod] 原因 + 分隔线保持不变。
static void write_mod_error(const std::wstring& logs_dir, const std::string& mod_name, const std::string& reason)
{
    cesium::log::mod_error(logs_dir, mod_name, reason);
}

static void set_env_w(const wchar_t* name, const std::wstring& value)
{
    SetEnvironmentVariableW(name, value.c_str());
}

// 启动环境: 游戏 / 引擎 / 进程。放在 mod 报告之前, 只打印一次。
//   - 游戏版本 = Unity 的 Application.version(bundleVersion), 取不到就省略;
//   - 引擎版本 = UnityPlayer.dll 的版本资源(不依赖 il2cpp, 兜底信息);
//   - 进程号 = 用来和 Windows 任务管理器/崩溃转储对上号。
// 这里任何一项取不到都只少一个字段, 不影响引导。
static void log_environment(Il2CppDomain* domain)
{
    std::wstring unity = file_version(parent_dir(loader_root()) + L"\\UnityPlayer.dll");
    const size_t cut = unity.find(L" (");   // "2022.3.62f3 (96770f904ca7)" → 只留版本号
    if (cut != std::wstring::npos) unity = unity.substr(0, cut);

    const std::string app_version = boot::application_version(domain);

    wchar_t exe[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    const std::string exe_name = utf8_from_wide(file_name_of(exe).c_str());

    // shell 的 status/env 要复用这两个版本号与 exe 路径, 存进运行期快照。
    cesium::runtime::set_versions(app_version, utf8_from_wide(unity.c_str()));
    cesium::runtime::set_exe_path(exe);

    std::string line = "[loader] ";
    if (!app_version.empty()) line += exe_name + " " + app_version + " · ";
    line += "engine Unity " + (unity.empty() ? std::string("unknown") : utf8_from_wide(unity.c_str()));
    line += " · pid " + std::to_string(GetCurrentProcessId()) + " (x64)";

    console_set_color(LOG_MAGENTA);
    log_line(line);
    console_set_color(LOG_DEFAULT);
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
    log_line("[loader] version.dll Doorstop boot thread started (enabled=" + std::string(cfg.enabled ? "true" : "false") + ")");

    // 3. 总开关: disabled 时静默退出, 游戏原样运行
    if (!cfg.enabled)
    {
        log_line("[loader] doorstop_config.json enabled=false, skipping boot");
        return 0;
    }

    // 运行期状态快照: shell 运行在另一个线程, 从这里读配置/版本/mod 列表。
    cesium::runtime::mark_started();
    cesium::runtime::set_config(cfg);
    cesium::runtime::set_forward_enabled(cfg.forwardActivityLog);

    // 3.5 变速引擎: 在 loader lock 释放后安装 hook(不在 DllMain 里做, 避免
    // ERROR_DLL_INIT_FAILED / 内存竞态)。游戏进程已初始化, hook 生效后
    // 游戏感知的时间从此刻开始缩放。
    speedhack_init();

    // 3.6 基础倍率: 若配置了非 1.0 倍率, 立即应用并一直保持
    // (游戏启动即变速, 无需等 mod 初始化)。
    if (cfg.speedhackBaseSpeed > 0.0 && cfg.speedhackBaseSpeed != 1.0)
    {
        if (speedhack_set_speed(cfg.speedhackBaseSpeed))
            log_line("[loader] base speed " + speed_text(cfg.speedhackBaseSpeed) + " (kept for the whole session)");
        else
            log_line("[loader] failed to apply base speed: " + speed_text(cfg.speedhackBaseSpeed));
    }

    // 3.7 变速控制文件通道(见 speedctl.h): 热更程序集无法 P/Invoke, mod 只能通过文件请求倍率。
    //     放在这里(=引擎就绪后立刻)而不是等 GameAssembly: 通道越早就绪越好, 且与加载流程解耦。
    //     必须在加载 mod 之前 —— mod 初始化时会读 state.txt 判断引擎是否可用。
    if (cfg.speedControlEnabled)
        speedctl_start(speedctl_dir(), cfg.speedhackBaseSpeed);
    else
        log_line("[loader] speedControlEnabled=false, skipping speed control file channel");

    // 3.7.1 窗口置前控制文件通道(见 windowctl.h): 同样因为热更程序集无法 P/Invoke,
    //       mod 只能写请求文件, 由原生侧调用 SetForegroundWindow。
    if (cfg.windowControlEnabled)
        windowctl_start(windowctl_dir());
    else
        log_line("[loader] windowControlEnabled=false, skipping foreground control file channel");

    // 3.7.2 CesiumLoader Debug Shell: 控制台就绪后启动(配置 debugShell 默认关闭),
    //       之后游戏加载 mod 的几秒里就能查状态/改配置。没有控制台时 shell_start 返回 false。
    if (cfg.consoleEnabled && cfg.debugShell)
    {
        if (cesium::shell::shell_start())
        {
            console_set_color(LOG_CYAN);
            log_line("[shell] CesiumLoader Debug Shell ready (type 'help')");
            console_set_color(LOG_DEFAULT);
        }
        else
        {
            log_line("[shell] failed to start, running in log-only mode");
        }
    }
    else if (cfg.consoleEnabled)
    {
        log_line("[shell] debugShell=false, console input disabled");
    }

    // 3.7.3 活动日志转发线程: 常驻, 是否真正转发由 forwardActivityLog 开关控制,
    //       这样 shell 里可以随时开关(线程本身不重启)。
    forward::start_activity_forward_thread();

    // 4. 等待 GameAssembly.dll
    HMODULE ga = boot::wait_game_assembly(cfg.gameAssemblyTimeoutSec);
    if (!ga) { log_line("[loader] GameAssembly.dll timed out"); return 0; }
    log_line("[loader] GameAssembly.dll loaded");

    if (!boot::resolve_il2cpp(ga)) { log_line("[loader] failed to resolve il2cpp exports"); return 0; }

    Il2CppDomain* domain = nullptr;
    for (;;)
    {
        domain = boot::domain_get();
        if (domain) break;
        if (GetTickCount64() - started > (ULONGLONG)cfg.domainTimeoutSec * 1000) { log_line("[loader] il2cpp domain timed out"); return 0; }
        Sleep(200);
    }
    boot::thread_attach(domain);
    log_line("[loader] il2cpp runtime ready, waiting for HybridCLR hot update...");

    // 3.8 Steam 绕过(见 steamhack.h)。插入点很关键: 必须在 thread_attach 之后
    //     (IL2CPP 就绪、已挂到运行时上)、wait_hybridclr 之前(此时枚举到的就是 AOT 镜像,
    //     且 AOT 的 SteamManager.Awake() 要到进程启动约 7s 后才执行, 窗口充裕)。
    //     阶段1 = 自杀门(受 steamBypassEnabled / steamBypassRestartCheck 控制),
    //     阶段2 = 大厅匹配(受 steamBypassMatchmaking 控制, 修"创建/加入房间点了没反应"),
    //     阶段3 = Nullable<Lobby>.get_HasValue 恒 false(受 steamBypassLobbyHasValue 控制)。
    //             **实测作废**: HybridCLR 解释器把 Nullable.HasValue 当内建指令内联, 这个 hook
    //             从未被命中(实机日志里没有"首次被拦截"那行)。hook 保留但无害, 它不是解法。
    //     方案C = Task<T>..ctor(T) 按**原生 ABI 直调** methodPointer, 绕过 il2cpp_runtime_invoke
    //             对 Nullable<Lobby> 的错误编组(受 steamBypassTaskCtorMode 控制, 默认 "auto")。
    //     方案B = Steamworks.Data.Lobby.SetPublic/SetJoinable 挂 no-op、Id 是属性则 get_Id 恒返回 0
    //             (受 steamBypassLobbyMethods 控制, **默认 false**) —— 备用安全网。
    //             注意: 该开关**默认必须是 false** —— 实机证明 true 会让游戏启动早期崩溃
    //             (0xC0000005 / GameAssembly.dll), 详见 config.h 里同名字段的注释。
    //     阶段5 = Steamworks.Data.LobbyQuery.RequestAsync() 恒返回"结果为空 Lobby[] 的已完成 Task"
    //             (受 steamBypassLobbyQuery 控制, **默认 true**)。实机实测: 退房/解散/被踢时 6 条 NRE
    //             抛在 `await ((LobbyQuery)(ref lobbyList)).RequestAsync()` 上(不是 LobbyList 取值本身),
    //             让 RoomLogic.cs:398/401 与 :567-573 全部被跳过 —— 本地房间状态不清、被踢提示不弹、
    //             不返回房间列表页。返回长度 0 的数组后 `array.Length > 0` 为 false, 这几段正常执行。
    //             它是**独立**的一层: 不依赖阶段2 的动态返回值工厂, 只受自己那一个开关控制。
    //     **失败只记日志, 绝不阻止游戏启动** —— 绕过没装上时游戏行为与原版一致。
    if (cfg.steamBypassEnabled || cfg.steamBypassMatchmaking || cfg.steamBypassLobbyQuery)
    {
        steamhack_install(boot::safe_il2cpp(), cfg.steamBypassEnabled,
                          cfg.steamBypassRestartCheck, cfg.steamBypassMatchmaking,
                          cfg.steamBypassLobbyHasValue,
                          cfg.steamBypassTaskCtorMode, cfg.steamBypassLobbyMethods,
                          cfg.steamBypassLobbyQuery);
    }
    else
    {
        log_line("[steamhack] all bypass switches off, skipping Steam bypass (vanilla behavior)");
    }

    if (!boot::wait_hybridclr(domain, cfg.hybridclrTimeoutSec)) { log_line("[loader] AstralParty.Runtime timed out"); return 0; }
    log_line("[loader] HybridCLR hot update ready");
    log_environment(domain);

    // 5. 设置环境变量, 供托管引导程序与 mod 读取
    std::wstring mods = mods_dir();
    std::wstring logs = logs_dir();
    std::wstring sdk = sdk_dir();
    std::wstring boot = bootstrap_dir();
    std::wstring speed = speedctl_dir();
    std::wstring window = windowctl_dir();
    set_env_w(L"CESIUM_MODS_DIR", mods);
    set_env_w(L"CESIUM_LOG_DIR", logs);
    set_env_w(L"CESIUM_SDK_DIR", sdk);
    set_env_w(L"CESIUM_BOOTSTRAP_DIR", boot);
    set_env_w(L"CESIUM_SPEED_DIR", speed);
    set_env_w(L"CESIUM_WINDOW_DIR", window);

    // 本次启动的日志文件(**每次启动一套, 不共用**)。
    // 托管侧(SDK 的 SdkLog / SdkDiagnostics / Bootstrap)也要往"这一次"的文件里写, 而它只知道
    // 目录、推不出文件名, 所以把完整路径一起传下去 —— 只给 CESIUM_LOG_DIR 的话, 托管侧就只能
    // 写死一个固定名字, 那样几次启动又混到同一个文件里了。
    const std::wstring log_file = cesium::log::loader_log_path();
    const std::wstring activity_file = cesium::log::activity_log_path(logs);
    const std::wstring error_file = cesium::log::error_log_path(logs);
    set_env_w(L"CESIUM_LOG_FILE", log_file);
    set_env_w(L"CESIUM_ACTIVITY_LOG_FILE", activity_file);
    set_env_w(L"CESIUM_ERROR_LOG_FILE", error_file);

    // 路径只在"根"上打印一次绝对路径, 其余一律用 <loader>\ 短形式。
    // 旧实现把同一个 90 列前缀重复了 8 次, 其中"本次启动日志"一行 494 列:
    // 窄窗口里折成 4~5 行、续行没有缩进, 一条逻辑行看起来像四条。
    log_line(L"[loader] root " + loader_root());
    console_set_color(LOG_DIM);
    log_line(L"[loader] subdirs mods\\ sdk\\ bootstrap\\ logs\\ speed\\ window\\");
    console_set_color(LOG_DEFAULT);
    log_line(L"[loader] boot log " + short_path(log_file));
    log_line(L"[loader] mod log " + short_path(activity_file));
    log_line(L"[loader] error log " + short_path(error_file));

    // 6a. 实验特性: useManagedBootstrap=true 时, 加载 bootstrap DLL 并调用入口,
    //     由托管代码负责 sdk/mods 加载与入口调用(注意 HybridCLR 反射裁剪风险)。
    //     默认 false: 走下面 6b 的原生编排(可靠路径)。
    if (cfg.useManagedBootstrap)
    {
        fs::path bootstrap_path = fs::path(boot) / fs::path(cfg.bootstrapAssembly);
        if (!fs::exists(bootstrap_path))
        {
            log_line("[loader] bootstrap assembly not found: " + cfg.bootstrapAssembly);
            return 0;
        }
        std::ifstream in(bootstrap_path, std::ios::binary);
        if (!in) { log_line("[loader] failed to read bootstrap: " + cfg.bootstrapAssembly); return 0; }
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        log_line("[loader] loading bootstrap: " + cfg.bootstrapAssembly + " (" + std::to_string(bytes.size()) + " bytes)");

        std::string err;
        Il2CppAssembly* asm_ = boot::load_assembly_bytes(domain, bytes, err);
        if (!asm_) { log_line("[loader] bootstrap Assembly.Load failed: " + err); return 0; }
        log_line("[loader] bootstrap Assembly.Load(byte[]) succeeded");

        if (!boot::run_entry(asm_, cfg.bootstrapType.c_str(), cfg.bootstrapMethod.c_str(), err))
        {
            log_line("[loader] bootstrap entry failed: " + err);
            return 0;
        }
        log_line("[loader] bootstrap entry ran successfully, boot thread finished");
        return 0;
    }

    // 6b. 原生编排(默认): 加载 sdk 依赖, 枚举 mods, 逐个 Assembly.Load + 调用入口。
    //     全部走 il2cpp 原生 API, 不受 HybridCLR AOT 反射裁剪影响 —— 已验证可靠。

    // 先加载 SDK 依赖(不调入口): 供 mods 引用。
    //
    // **必须按依赖顺序加载**, 不能只按文件名排序: HybridCLR 在 Assembly.Load 时就要把被加载
    // 程序集的每个 AssemblyRef 解析成"已加载的 Il2CppAssembly", 依赖缺失就直接抛异常
    // (实测: "Serilog.Sinks.File.dll" 按文件名排在 "Serilog.dll" 之前 → 前者必然加载失败)。
    // 这里改成"多轮重试, 直到某一轮没有任何进展": 每轮把能加载的加载掉, 剩下的留到下一轮,
    // 这样任意命名与依赖深度的闭包都能正确加载, 同时失败项会被逐条报告出来。
    if (fs::exists(sdk))
    {
        std::vector<fs::path> pending;
        for (auto& entry : fs::directory_iterator(sdk))
        {
            if (entry.is_regular_file() && _stricmp(entry.path().extension().string().c_str(), ".dll") == 0)
                pending.push_back(entry.path());
        }
        std::sort(pending.begin(), pending.end());   // 仅用于保证每轮的顺序确定

        std::map<std::string, std::string> last_error;   // 名字 -> 最近一次失败原因
        int round = 0;
        while (!pending.empty())
        {
            ++round;
            std::vector<fs::path> next;
            for (auto& dll : pending)
            {
                std::string name = dll.stem().string();
                std::ifstream in(dll, std::ios::binary);
                if (!in) { log_line("[loader] SDK " + name + " read failed"); continue; }
                std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
                std::string err;
                if (boot::load_assembly_bytes(domain, bytes, err))
                {
                    log_line("[loader] SDK " + name + " loaded" + (round > 1 ? (" (round " + std::to_string(round) + ")") : ""));
                }
                else
                {
                    last_error[name] = err;
                    next.push_back(dll);
                }
            }
            // 这一轮一个都没成功 → 剩下的依赖缺口无法通过再试解决, 跳出
            if (next.size() == pending.size()) { pending.swap(next); break; }
            pending.swap(next);
        }
        for (auto& dll : pending)
        {
            std::string name = dll.stem().string();
            auto it = last_error.find(name);
            log_line("[loader] SDK " + name + " load failed: " + (it == last_error.end() ? std::string("unknown") : it->second));
        }
    }

    if (!fs::exists(mods))
    {
        log_line(L"[loader] mods dir missing/unreadable: " + short_path(mods));
        return 0;
    }

    // ---- 扫描 mod 列表 ----
    // 新布局: mods\{ModId}\{ModId}.dll (每 mod 一个文件夹, sidecar 在文件夹内 {ModId}.json)
    // 兼容旧布局: mods\{ModId}.dll 平铺(直接放 mods 根下的 dll 仍加载)
    std::map<std::string, cesium::ModLoc> mod_locs;
    for (auto& loc : cesium::scan_mods_dir(utf8_from_wide(mods.c_str())))
        mod_locs[loc.id] = loc;

    // 读所有 sidecar, 用于依赖解析 + SDK 版本协商
    std::map<std::string, cesium::ModMeta> metas;
    for (auto& kv : mod_locs)
    {
        const std::string& n = kv.first;
        if (!kv.second.sidecar.empty())
        {
            cesium::ModMeta m = cesium::parse_sidecar(cesium::read_sidecar_text(kv.second.sidecar));
            if (m.hasSidecar) metas[n] = m;
        }
    }

    // 按依赖拓扑排序 + SDK 版本检查 + 缺失依赖报告
    std::vector<std::string> stems;
    for (auto& kv : mod_locs) stems.push_back(kv.first);
    std::vector<std::string> rejected;
    std::vector<std::string> ordered = cesium::sort_mods_by_deps(stems, metas, cfg.sdkVersion, &rejected);
    // 报告已禁用(不加载)的 mod
    std::vector<std::string> disabled;
    for (auto& kv : mod_locs)
    {
        std::string n = kv.first;
        auto it = metas.find(n);
        if (it != metas.end() && it->second.hasSidecar && !it->second.enabled)
            disabled.push_back(n);
    }
    // 按依赖序重建要加载的列表(排除禁用)
    std::vector<std::string> to_load;
    for (auto& name : ordered)
        if (std::find(disabled.begin(), disabled.end(), name) == disabled.end())
            to_load.push_back(name);
    std::sort(disabled.begin(), disabled.end());
    std::sort(rejected.begin(), rejected.end());
    std::sort(to_load.begin(), to_load.end());

    // ---- mod 报告 ----
    // 布局原则:
    //   1. 一条事实只出现一次 —— 旧实现在"发现"和"加载结果"里把 mod 名各列一遍,
    //      还带着与"发现 11 个"自相矛盾的 [1/9] 编号;
    //   2. 对齐按**显示宽度**(中文/emoji 算 2 列), 列宽取当前这批判 mod 的实际最大值;
    //   3. 每个 mod 一行: 序号 / 名字 / 版本 / 结果, 结果列直接给结论(成功带耗时)。
    auto version_of = [&](const std::string& id) -> std::string
    {
        auto it = metas.find(id);
        return (it != metas.end() && !it->second.version.empty()) ? it->second.version : "-";
    };
    auto pad_right = [](const std::string& s, int width) -> std::string
    {
        const int cur = text_width(s);
        return cur >= width ? s : s + std::string(static_cast<size_t>(width - cur), ' ');
    };

    int name_w = text_width("mod");
    int ver_w = text_width("version");
    for (auto& n : to_load)  { name_w = (std::max)(name_w, text_width(n)); ver_w = (std::max)(ver_w, text_width(version_of(n))); }
    for (auto& n : disabled) name_w = (std::max)(name_w, text_width(n));
    for (auto& n : rejected) name_w = (std::max)(name_w, text_width(n));
    name_w += 2;
    ver_w += 2;

    auto row = [&](const std::string& idx, const std::string& name, const std::string& ver,
                   const std::string& result, WORD color)
    {
        console_set_color(color);
        log_line("  " + pad_right(idx, 3) + pad_right(name, name_w) + pad_right(ver, ver_w) + result);
        console_set_color(LOG_DEFAULT);
    };

    console_set_color(LOG_CYAN);
    log_line("──────────────────────────────────────────────");
    log_line(" CesiumLoader v" + std::string(cesium::config::LoaderConfig::loaderVersion) +
             " · SDK v" + cfg.sdkVersion + " · mod load report");
    log_line("──────────────────────────────────────────────");
    console_set_color(LOG_DEFAULT);

    console_set_color(LOG_WHITE);
    log_line("▶ mod load · found " + std::to_string(mod_locs.size()) + " (enabled " +
             std::to_string(to_load.size()) + " · disabled/skipped " +
             std::to_string(disabled.size() + rejected.size()) + ")");
    console_set_color(LOG_DIM);
    log_line("  " + pad_right("#", 3) + pad_right("mod", name_w) + pad_right("version", ver_w) + "result");
    console_set_color(LOG_DEFAULT);
    int okCount = 0, failCount = 0;
    // shell 的 mods 命令要的是一份"每个 mod 一行结论"的快照(data/ 里没有现成的)。
    std::vector<cesium::runtime::ModStatus> statuses;
    for (size_t i = 0; i < to_load.size(); i++)
    {
        const std::string& name = to_load[i];
        auto it = mod_locs.find(name);
        if (it == mod_locs.end()) continue;
        fs::path dll = it->second.dll;
        const std::string idx = std::to_string(i + 1);
        const std::string ver = version_of(name);
        const ULONGLONG t0 = GetTickCount64();

        std::ifstream in(dll, std::ios::binary);
        if (!in)
        {
            row(idx, name, ver, "dll read failed", LOG_RED);
            write_mod_error(logs, name, "DLL read failed");
            statuses.push_back({ name, ver, "dll read failed", true, false });
            failCount++;
            continue;
        }
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());

        std::string err;
        Il2CppAssembly* asm_ = boot::load_assembly_bytes(domain, bytes, err);
        if (!asm_)
        {
            row(idx, name, ver, "load failed: " + err, LOG_RED);
            write_mod_error(logs, name, "Assembly.Load failed: " + err);
            statuses.push_back({ name, ver, "load failed: " + err, true, false });
            failCount++;
            continue;
        }

        std::string entry_type = name + ".ModEntry";
        if (boot::run_entry(asm_, entry_type.c_str(), "Main", err))
        {
            row(idx, name, ver, "ok  " + fmt_ms(GetTickCount64() - t0), LOG_GREEN);
            statuses.push_back({ name, ver, "ok  " + fmt_ms(GetTickCount64() - t0), true, true });
            okCount++;
        }
        else
        {
            row(idx, name, ver, "entry failed: " + err, LOG_RED);
            write_mod_error(logs, name, "entry failed: " + err);
            statuses.push_back({ name, ver, "entry failed: " + err, true, false });
            failCount++;
        }
    }

    // 没加载的也进同一张表: 用户需要知道"哪些没加载、为什么", 而不是自己去数差集。
    for (auto& n : disabled)
    {
        row("-", n, "-", "disabled", LOG_DIM);
        statuses.push_back({ n, "-", "disabled", false, false });
    }
    for (auto& n : rejected)
    {
        row("-", n, "-", "skipped (missing dep / SDK version)", LOG_YELLOW);
        statuses.push_back({ n, "-", "skipped (missing dep / SDK version)", true, false });
    }

    // 报告已生成, 把同一份结论交给运行期快照(shell 的 mods 命令用)。
    cesium::runtime::set_mods(std::move(statuses));

    // 警告: 声明了"操作游戏"能力(GameActions=2)的 mod —— 仅提示, 不阻止。
    // 权限机制已取消: 任何 mod 都能调用 SDK 的模拟操作 API, 此处仅告知用户。
    // 放在表格之后: 插在清单中间会把同组信息切断。
    for (auto& kv : metas)
    {
        const auto& m = kv.second;
        if (m.hasSidecar && (m.permissions & 2) != 0 && m.enabled)
        {
            console_set_color(LOG_YELLOW);
            log_line("⚠ " + kv.first + " declares the game-actions (simulated input) capability; verify the source is trusted");
            console_set_color(LOG_DEFAULT);
        }
    }

    // ---- 总结 ----
    console_set_color(LOG_WHITE);
    log_line("summary " + std::to_string(okCount) + " ok · " + std::to_string(failCount) + " failed · " +
             std::to_string(disabled.size() + rejected.size()) + " not loaded · " +
             fmt_seconds(GetTickCount64() - started));
    console_set_color(LOG_DIM);
    log_line("[loader] boot thread finished");
    console_set_color(LOG_DEFAULT);
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

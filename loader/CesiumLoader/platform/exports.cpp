// exports.cpp - 加载器核心 (CesiumLoader.dll) 的对外导出 + DllMain
//
// 核心由薄代理 version.dll 在游戏进程里加载:
//   代理 DllMain → 后台线程 → LoadLibraryW(<代理目录>\CesiumLoader.dll)
//     → 读 CesiumLoaderAbiVersion() 比对 ABI → 调用 CesiumLoaderStart() → 引导线程
// 契约(核心文件名 / ABI 号 / 两个入口)在 loader\shared\cesium_core_abi.h, 两侧共用一份。
//
// 本文件只剩三类导出:
//   * CesiumLoaderAbiVersion / CesiumLoaderStart —— 代理层契约(见上);
//   * ap_speed_set / ap_speed_get / ap_speed_active —— 变速的旧原生接口。代理会把它们
//     桥接过去保兼容(SDK 与热更模组不走这里: 热更程序集无法 P/Invoke, 用的是 speed\ 下
//     的文件 / 本地 UDP 通道);
//   * ap_console_write / ap_console_write_w —— 定义在 platform\console.cpp。
//
// 代理层那部分(转发系统 version.dll 的 15 个导出、加载核心、写"核心没起来"的面包屑日志)
// 在 loader\CesiumLoader.Proxy\proxy.cpp, 与本文件互不依赖, 只共享上面那份契约头。

#include "platform/loader.h"
#include "cesium_core_abi.h"

#include "config/config.h"
#include "speed/speedhack.h"
#include "speed/speedctl.h"
#include "steam/steamhack.h"

#include <windows.h>

// ---------- 引导线程启动 ----------

static volatile LONG g_boot_started = 0;

void maybe_start_boot()
{
    if (InterlockedCompareExchange(&g_boot_started, 1, 0) == 0)
    {
        boot_il2cpp_and_load_mods();
    }
}

// ---------- 代理层契约 ----------

// 代理加载本 DLL 后先读这个值, 与自己编译时的 CESIUM_CORE_ABI_VERSION 比对:
// 不一致说明代理与核心不是同一次发布的产物(手动覆盖 / 回滚 / 半装), 代理会拒绝启动并
// 在自己目录的 logs\proxy-*.log 里写清楚 —— 比"模组莫名其妙不加载"好查。
extern "C" __declspec(dllexport) unsigned int WINAPI CesiumLoaderAbiVersion()
{
    return CESIUM_CORE_ABI_VERSION;
}

// 引导入口(幂等)。代理在 ABI 比对通过后调用; 重复调用只会启动一次引导线程。
extern "C" __declspec(dllexport) BOOL WINAPI CesiumLoaderStart()
{
    maybe_start_boot();
    return TRUE;
}

// ---------- 自定义导出: 变速引擎 (原生调用方经代理 version.dll 的 ap_* 桥接进来) ----------

extern "C" __declspec(dllexport) BOOL WINAPI ap_speed_set(double speed)
{
    return speedctl_set_speed(speed) ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) double WINAPI ap_speed_get()
{
    return speedhack_get_speed();
}

extern "C" __declspec(dllexport) BOOL WINAPI ap_speed_active()
{
    return speedhack_active() ? TRUE : FALSE;
}

// ---------- 版本横幅 ----------

// 日志文件的第一段: 打印加载器/SDK 版本与两个项目的 GitHub 地址,
// 让用户在日志头就能确认装的是哪一版、去哪里看更新。
// 版本全部取自 config/config.h 的编译期常量(发布时与 tag 一起更新),
// 不依赖 doorstop_config.json —— DllMain 时尚未加载配置。
// 注意: 这里只写文件日志(控制台尚未初始化), 且只做字符串拼接, 不读任何外部文件。
static void log_version_banner()
{
    const std::string loader_version = LoaderConfig::loaderVersion;
    const LoaderConfig defaults;  // 只读编译期默认值里的 sdkVersion(与分发布局 doorstop_config.json 同源)
    const std::string sdk_version = defaults.sdkVersion;

    console_set_color(LOG_CYAN);
    log_line("[loader] ──────────────────────────────────────────────");
    log_line("[loader] CesiumLoader v" + loader_version + " (version.dll -> CesiumLoader.dll, native) · SDK v" + sdk_version);
    log_line("[loader] CesiumLoader: https://github.com/higashitaniyume/CesiumLoader");
    log_line("[loader] CesiumLoader Releases: https://github.com/higashitaniyume/CesiumLoader/releases");
    log_line("[loader] AstralParty.Toys: https://github.com/higashitaniyume/AstralParty.Toys");
    log_line("[loader] AstralParty.Toys Releases: https://github.com/higashitaniyume/AstralParty.Toys/releases");
    log_line("[loader] ──────────────────────────────────────────────");
    console_set_color(LOG_DEFAULT);
}

// ---------- DllMain ----------

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        // 只写版本横幅。这里可能在 loader lock 下被调用(由代理的加载线程 LoadLibrary 而来),
        // 所以不做任何重活: 控制台 / hook / 目录扫描全部留给引导线程(见 runtime\loader.cpp)。
        log_version_banner();
        log_line("[loader] core CesiumLoader.dll loaded (DLL_PROCESS_ATTACH)");
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // 先摘掉 steamhack 自己的 hook, 再交给 speedhack 收尾 ——
        // speedhack_shutdown() 会 MH_Uninitialize(), 卸载整个 MinHook(speedhack 拥有它)。
        steamhack_uninstall();
        speedhack_shutdown();
    }
    return TRUE;
}

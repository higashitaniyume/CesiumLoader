// exports.cpp - winmm.dll 导出转发(22 个 UnityPlayer 导入的精确签名) + DllMain
//
// UnityPlayer.dll 从 winmm.dll 导入 22 个函数(经 objdump 确认)。
// 这里逐个精确签名转发到系统 C:\Windows\System32\winmm.dll,
// 保证 AkSoundEngine/CRI 初始化正常(AkSoundEngine 等还会调用 gen_winmm
// 生成的其余 157 个导出,见 generated_forwards.cpp)。
//
// 惰性引导:不在 DllMain(loader lock) 里创建线程,改为首次转发调用时启动,
// 此时 loader lock 已释放,避免拖慢进程启动阶段的所有 DLL 加载。

#include "loader.h"

#include <windows.h>

// ---------- 转发基础设施 ----------

static HMODULE g_real_winmm = nullptr;

void* real_winmm_handle()
{
    if (!g_real_winmm)
    {
        g_real_winmm = LoadLibraryA("C:\\Windows\\System32\\winmm.dll");
        log_line("[hijack] 系统 winmm.dll 句柄: (loaded)");
    }
    return g_real_winmm;
}

void* real_winmm_fn(const char* name)
{
    return reinterpret_cast<void*>(GetProcAddress(reinterpret_cast<HMODULE>(real_winmm_handle()), name));
}

// ---------- 引导线程启动 ----------

static volatile LONG g_boot_started = 0;

void maybe_start_boot()
{
    if (InterlockedCompareExchange(&g_boot_started, 1, 0) == 0)
    {
        boot_il2cpp_and_load_mods();
    }
}

// ---------- 22 个 UnityPlayer 导入的精确签名转发 ----------
//
// 签名对照 mmsystem.h / timeapi.h(winmm 的 Windows SDK 头文件)。

using MMRESULT = UINT;
using HWAVE = HANDLE;
using DWORD_PTR = ULONG_PTR;

extern "C" __declspec(dllexport) UINT WINAPI waveOutGetDevCapsW(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveOutGetDevCapsW"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutGetDevCapsA(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveOutGetDevCapsA"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI timeGetTime(void)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void)>(real_winmm_fn("timeGetTime"));
    return fn();
}

extern "C" __declspec(dllexport) UINT WINAPI waveInAddBuffer(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveInAddBuffer"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutOpen(void* a, UINT b, void* c, void* d, void* e, UINT f)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, UINT, void*, void*, void*, UINT)>(real_winmm_fn("waveOutOpen"));
    return fn(a, b, c, d, e, f);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInStart(void* a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*)>(real_winmm_fn("waveInStart"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutPrepareHeader(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveOutPrepareHeader"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutUnprepareHeader(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveOutUnprepareHeader"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutWrite(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveOutWrite"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutReset(void* a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*)>(real_winmm_fn("waveOutReset"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutGetPosition(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveOutGetPosition"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInGetNumDevs(void)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void)>(real_winmm_fn("waveInGetNumDevs"));
    return fn();
}

extern "C" __declspec(dllexport) UINT WINAPI waveInGetDevCapsA(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveInGetDevCapsA"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInGetDevCapsW(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveInGetDevCapsW"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInOpen(void* a, UINT b, void* c, void* d, void* e, UINT f)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, UINT, void*, void*, void*, UINT)>(real_winmm_fn("waveInOpen"));
    return fn(a, b, c, d, e, f);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInClose(void* a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*)>(real_winmm_fn("waveInClose"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInPrepareHeader(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveInPrepareHeader"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInUnprepareHeader(void* a, void* b, UINT c)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*, void*, UINT)>(real_winmm_fn("waveInUnprepareHeader"));
    return fn(a, b, c);
}

extern "C" __declspec(dllexport) UINT WINAPI timeBeginPeriod(UINT a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(UINT)>(real_winmm_fn("timeBeginPeriod"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI timeEndPeriod(UINT a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(UINT)>(real_winmm_fn("timeEndPeriod"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutClose(void* a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*)>(real_winmm_fn("waveOutClose"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI waveInReset(void* a)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void*)>(real_winmm_fn("waveInReset"));
    return fn(a);
}

extern "C" __declspec(dllexport) UINT WINAPI waveOutGetNumDevs(void)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<UINT(WINAPI*)(void)>(real_winmm_fn("waveOutGetNumDevs"));
    return fn();
}

// ---------- DllMain ----------

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        log_line("[hijack] winmm.dll 被加载 (DLL_PROCESS_ATTACH)");
        // 注意: 不在 loader lock 下创建线程; 由首个 fwd 转发调用触发 maybe_start_boot。
    }
    return TRUE;
}

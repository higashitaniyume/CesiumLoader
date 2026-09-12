// speedhack.cpp - 变速引擎实现
//
// 借鉴 speedhack-rs (https://github.com/Hirtol/speedhack-rs) 的设计:
//   - hook 4 个系统时间函数: GetTickCount / GetTickCount64 / timeGetTime /
//     QueryPerformanceCounter, 按倍率缩放返回值, 让游戏感知的时间变快/变慢
//   - hook 机制用 MinHook (微软生态事实标准的 inline hook 库, CE/BepInEx 同款):
//     自动处理 x64 指令重定位 / 指令边界解码 / 多线程安全, 避免自研 hook 的坑
//   - 缩放算法与 speedhack-rs 的 TimeState 等价: 每个 API 记录 basetime/offset,
//     虚拟 = offset + (real - basetime) * speed; 切换倍率时重设 basetime/offset
//     保证时间连续不跳变
//
// 与 CheatEngine/speedhack 相同的效果。变速会影响游戏感知的所有时间
// (动画/回合/网络超时), 联机对局慎用。

#include "speedhack.h"

#include "loader.h"

#include <atomic>
#include <cstdint>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mmsystem.h>   // timeGetTime

#include <MinHook.h>

// ---------- 缩放状态 (与 speedhack-rs TimeState 等价) ----------

namespace
{

// 每个 API 独立记录: basetime(最近一次设速时的真实时间) + offset(当时的虚拟时间)
struct TimeState
{
    double speed;

    uint32_t gtc_basetime;
    uint32_t gtc_offset;

    uint64_t gtc64_basetime;
    uint64_t gtc64_offset;

    uint32_t tgt_basetime;
    uint32_t tgt_offset;

    int64_t qpc_basetime;
    int64_t qpc_offset;
};

std::atomic<TimeState> g_state{ {1.0, 0,0, 0,0, 0,0, 0,0} };
std::atomic<bool> g_enabled{ false };

inline double load_speed()
{
    double s = g_state.load(std::memory_order_relaxed).speed;
    return s <= 0.0 ? 1.0 : s;
}

// 计算当前"虚拟时间"(用旧 state), 供切换倍率时保持连续
inline uint32_t virt_gtc(const TimeState& st, uint32_t real)
{
    return st.gtc_offset + (uint32_t)((double)(real - st.gtc_basetime) * st.speed);
}
inline uint64_t virt_gtc64(const TimeState& st, uint64_t real)
{
    return st.gtc64_offset + (uint64_t)((double)(real - st.gtc64_basetime) * st.speed);
}
inline uint32_t virt_tgt(const TimeState& st, uint32_t real)
{
    return st.tgt_offset + (uint32_t)((double)(real - st.tgt_basetime) * st.speed);
}
inline int64_t virt_qpc(const TimeState& st, int64_t real)
{
    return st.qpc_offset + (int64_t)((double)(real - st.qpc_basetime) * st.speed);
}

// ---------- hook 回调 (retour 的 real_xxx 对应: 调原函数 → 缩放返回) ----------

// 真实函数指针 (由 MinHook 填充 trampoline)
using Fn_GetTickCount = ULONG(WINAPI*)();
using Fn_GetTickCount64 = ULONGLONG(WINAPI*)();
using Fn_timeGetTime = DWORD(WINAPI*)();
using Fn_QPC = BOOL(WINAPI*)(LARGE_INTEGER*);

ULONG(WINAPI* Real_GetTickCount)();
ULONGLONG(WINAPI* Real_GetTickCount64)();
DWORD(WINAPI* Real_timeGetTime)();
BOOL(WINAPI* Real_QPC)(LARGE_INTEGER*);

ULONG WINAPI Hook_GetTickCount()
{
    ULONG real = Real_GetTickCount();
    TimeState st = g_state.load(std::memory_order_relaxed);
    if (st.speed == 1.0) return real;
    return virt_gtc(st, real);
}

ULONGLONG WINAPI Hook_GetTickCount64()
{
    ULONGLONG real = Real_GetTickCount64();
    TimeState st = g_state.load(std::memory_order_relaxed);
    if (st.speed == 1.0) return real;
    return virt_gtc64(st, real);
}

DWORD WINAPI Hook_timeGetTime()
{
    DWORD real = Real_timeGetTime();
    TimeState st = g_state.load(std::memory_order_relaxed);
    if (st.speed == 1.0) return real;
    return virt_tgt(st, real);
}

BOOL WINAPI Hook_QPC(LARGE_INTEGER* lpCount)
{
    if (!lpCount) return Real_QPC(lpCount);
    BOOL ok = Real_QPC(lpCount);
    if (!ok) return ok;
    TimeState st = g_state.load(std::memory_order_relaxed);
    if (st.speed == 1.0) return ok;
    lpCount->QuadPart = virt_qpc(st, lpCount->QuadPart);
    return ok;
}

// ---------- MinHook 生命周期 ----------

bool g_hooked = false;

} // namespace

bool speedhack_init()
{
    if (g_enabled.load() || g_hooked) return g_hooked;

    if (MH_Initialize() != MH_OK)
    {
        log_line("[speedhack] MinHook 初始化失败");
        return false;
    }

    bool any = false;
    MH_STATUS st;

    st = MH_CreateHookApi(L"kernel32.dll", "GetTickCount", (LPVOID)&Hook_GetTickCount, (LPVOID*)&Real_GetTickCount);
    if (st == MH_OK) { any = true; } else { log_line("[speedhack] CreateHook GetTickCount: " + std::to_string((int)st)); }

    st = MH_CreateHookApi(L"kernel32.dll", "GetTickCount64", (LPVOID)&Hook_GetTickCount64, (LPVOID*)&Real_GetTickCount64);
    if (st == MH_OK) { any = true; } else { log_line("[speedhack] CreateHook GetTickCount64: " + std::to_string((int)st)); }

    st = MH_CreateHookApi(L"winmm.dll", "timeGetTime", (LPVOID)&Hook_timeGetTime, (LPVOID*)&Real_timeGetTime);
    if (st == MH_OK) { any = true; } else { log_line("[speedhack] CreateHook timeGetTime: " + std::to_string((int)st)); }

    st = MH_CreateHookApi(L"kernel32.dll", "QueryPerformanceCounter", (LPVOID)&Hook_QPC, (LPVOID*)&Real_QPC);
    if (st == MH_OK) { any = true; } else { log_line("[speedhack] CreateHook QueryPerformanceCounter: " + std::to_string((int)st)); }

    if (!any)
    {
        log_line("[speedhack] 全部 hook 创建失败, 变速不可用");
        MH_Uninitialize();
        return false;
    }

    // 初始化基准时间 (用真实函数, hook 尚未 enable)
    TimeState st0;
    st0.speed = 1.0;
    st0.gtc_basetime = st0.gtc_offset = GetTickCount();
    st0.gtc64_basetime = st0.gtc64_offset = GetTickCount64();
    st0.tgt_basetime = st0.tgt_offset = timeGetTime();
    LARGE_INTEGER q; QueryPerformanceCounter(&q);
    st0.qpc_basetime = st0.qpc_offset = q.QuadPart;
    g_state.store(st0);

    // 逐个 enable; 失败不影响其他
    int enabled = 0;
    if (MH_EnableHook(MH_ALL_HOOKS) == MH_OK) enabled = 4;
    else
    {
        // 逐个试
        if (MH_EnableHook((LPVOID)&Hook_GetTickCount) == MH_OK) enabled++;
        if (MH_EnableHook((LPVOID)&Hook_GetTickCount64) == MH_OK) enabled++;
        if (MH_EnableHook((LPVOID)&Hook_timeGetTime) == MH_OK) enabled++;
        if (MH_EnableHook((LPVOID)&Hook_QPC) == MH_OK) enabled++;
    }

    if (enabled == 0)
    {
        log_line("[speedhack] hook 启用失败, 变速不可用");
        MH_Uninitialize();
        return false;
    }

    g_hooked = true;
    g_enabled.store(true);
    log_line("[speedhack] 变速引擎就绪 (speed=1.0, hooks=" + std::to_string(enabled) + "/4)");
    return true;
}

void speedhack_shutdown()
{
    if (!g_hooked) return;
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
    g_hooked = false;
    g_enabled.store(false);
}

bool speedhack_set_speed(double speed)
{
    if (!g_enabled.load() || speed <= 0.0 || speed > 100.0) return false;

    // 与 speedhack-rs 相同: 先取当前真实时间与旧虚拟时间, 更新 basetime/offset
    // 注意: hook 已 enable, 必须经 Real_* (trampoline) 取真实时间, 不能调 API 本身
    TimeState oldSt = g_state.load();
    TimeState newSt;
    newSt.speed = speed;
    newSt.gtc_basetime = Real_GetTickCount();
    newSt.gtc64_basetime = Real_GetTickCount64();
    newSt.tgt_basetime = Real_timeGetTime();
    LARGE_INTEGER q; Real_QPC(&q);
    newSt.qpc_basetime = q.QuadPart;

    // offset = 旧虚拟时间 (保证切换倍率不跳变)
    newSt.gtc_offset = virt_gtc(oldSt, newSt.gtc_basetime);
    newSt.gtc64_offset = virt_gtc64(oldSt, newSt.gtc64_basetime);
    newSt.tgt_offset = virt_tgt(oldSt, newSt.tgt_basetime);
    newSt.qpc_offset = virt_qpc(oldSt, newSt.qpc_basetime);

    g_state.store(newSt);
    log_line("[speedhack] 倍率 -> " + std::to_string(speed));
    return true;
}

double speedhack_get_speed()
{
    return g_enabled.load() ? g_state.load().speed : 1.0;
}

bool speedhack_active()
{
    return g_enabled.load() && g_hooked;
}

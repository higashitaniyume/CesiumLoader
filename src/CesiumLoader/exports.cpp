// exports.cpp - version.dll 导出转发(Doorstop 式) + DllMain
//
// UnityPlayer.dll 从 version.dll 导入 3 个函数(GetFileVersionInfoSizeA /
// GetFileVersionInfoA / VerQueryValueA, 经 dumpbin 确认), 这里转发全部 15 个
// version.dll 导出到系统 C:\Windows\System32\version.dll,
// 保证 UnityPlayer 读取 exe 版本信息等行为正常。
//
// 惰性引导: 不在 DllMain(loader lock) 里创建线程, 改为首次转发调用时启动,
// 此时 loader lock 已释放, 避免拖慢进程启动阶段的所有 DLL 加载。
//
// 签名对照 Windows SDK versionapi.h / winver.h。

#include "loader.h"

#include <windows.h>

// ---------- 转发基础设施 ----------

static HMODULE g_real_version = nullptr;

void* real_version_handle()
{
    if (!g_real_version)
    {
        g_real_version = LoadLibraryA("C:\\Windows\\System32\\version.dll");
        log_line("[hijack] 系统 version.dll 句柄: (loaded)");
    }
    return g_real_version;
}

void* real_version_fn(const char* name)
{
    return reinterpret_cast<void*>(GetProcAddress(reinterpret_cast<HMODULE>(real_version_handle()), name));
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

// ---------- version.dll 15 个导出转发 ----------

extern "C" BOOL WINAPI GetFileVersionInfoA(LPCSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoA"));
    return fn(lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" BOOL WINAPI GetFileVersionInfoByHandle(HANDLE hFile, DWORD dwLen, LPVOID lpData)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(HANDLE, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoByHandle"));
    return fn(hFile, dwLen, lpData);
}

extern "C" BOOL WINAPI GetFileVersionInfoExA(DWORD dwFlags, LPCSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(DWORD, LPCSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoExA"));
    return fn(dwFlags, lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" BOOL WINAPI GetFileVersionInfoExW(DWORD dwFlags, LPCWSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(DWORD, LPCWSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoExW"));
    return fn(dwFlags, lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR lptstrFilename, LPDWORD lpdwHandle)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(LPCSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeA"));
    return fn(lptstrFilename, lpdwHandle);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeExA(DWORD dwFlags, LPCSTR lptstrFilename, LPDWORD lpdwHandle)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeExA"));
    return fn(dwFlags, lptstrFilename, lpdwHandle);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeExW(DWORD dwFlags, LPCWSTR lptstrFilename, LPDWORD lpdwHandle)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCWSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeExW"));
    return fn(dwFlags, lptstrFilename, lpdwHandle);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR lptstrFilename, LPDWORD lpdwHandle)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(LPCWSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeW"));
    return fn(lptstrFilename, lpdwHandle);
}

extern "C" BOOL WINAPI GetFileVersionInfoW(LPCWSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoW"));
    return fn(lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" DWORD WINAPI VerFindFileA(DWORD uFlags, LPCSTR szFileName, LPCSTR szWinDir, LPCSTR szAppDir, LPSTR szCurDir, PUINT lpuCurDirLen, LPSTR szDestDir, PUINT lpuDestDirLen)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT, LPSTR, PUINT)>(real_version_fn("VerFindFileA"));
    return fn(uFlags, szFileName, szWinDir, szAppDir, szCurDir, lpuCurDirLen, szDestDir, lpuDestDirLen);
}

extern "C" DWORD WINAPI VerFindFileW(DWORD uFlags, LPCWSTR szFileName, LPCWSTR szWinDir, LPCWSTR szAppDir, LPWSTR szCurDir, PUINT lpuCurDirLen, LPWSTR szDestDir, PUINT lpuDestDirLen)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT, LPWSTR, PUINT)>(real_version_fn("VerFindFileW"));
    return fn(uFlags, szFileName, szWinDir, szAppDir, szCurDir, lpuCurDirLen, szDestDir, lpuDestDirLen);
}

extern "C" DWORD WINAPI VerInstallFileA(DWORD uFlags, LPCSTR szSrcFileName, LPCSTR szDestFileName, LPCSTR szSrcDir, LPCSTR szDestDir, LPCSTR szCurDir, LPSTR szTmpFile, PUINT lpuTmpFileLen)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT)>(real_version_fn("VerInstallFileA"));
    return fn(uFlags, szSrcFileName, szDestFileName, szSrcDir, szDestDir, szCurDir, szTmpFile, lpuTmpFileLen);
}

extern "C" DWORD WINAPI VerInstallFileW(DWORD uFlags, LPCWSTR szSrcFileName, LPCWSTR szDestFileName, LPCWSTR szSrcDir, LPCWSTR szDestDir, LPCWSTR szCurDir, LPWSTR szTmpFile, PUINT lpuTmpFileLen)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT)>(real_version_fn("VerInstallFileW"));
    return fn(uFlags, szSrcFileName, szDestFileName, szSrcDir, szDestDir, szCurDir, szTmpFile, lpuTmpFileLen);
}

// 注意: 系统 version.dll 不导出 VerLanguageNameA/W(Win10/11 由 kernel32 导出),
// 因此这里也不转发, 与系统导出集保持一致(共 15 个)。

extern "C" BOOL WINAPI VerQueryValueA(LPCVOID pBlock, LPCSTR lpSubBlock, LPVOID* lplpBuffer, PUINT puLen)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCVOID, LPCSTR, LPVOID*, PUINT)>(real_version_fn("VerQueryValueA"));
    return fn(pBlock, lpSubBlock, lplpBuffer, puLen);
}

extern "C" BOOL WINAPI VerQueryValueW(LPCVOID pBlock, LPCWSTR lpSubBlock, LPVOID* lplpBuffer, PUINT puLen)
{
    maybe_start_boot();
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT)>(real_version_fn("VerQueryValueW"));
    return fn(pBlock, lpSubBlock, lplpBuffer, puLen);
}

// ---------- DllMain ----------

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        log_line("[hijack] version.dll 被加载 (DLL_PROCESS_ATTACH)");
        // 直接启动引导线程(而非等首次转发调用):
        // 某些游戏(如 Astral Party)加载了 version.dll 但从不调用其导出,
        // 惰性触发会永远不启动。boot_thread 开头会 Sleep 避开 loader lock,
        // 且引导过程中不 LoadLibrary 任何 DLL, 因此在 DllMain 创建线程是安全的。
        // maybe_start_boot 的 InterlockedCompareExchange 保证只启动一次(双保险)。
        maybe_start_boot();
    }
    return TRUE;
}

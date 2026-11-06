// overlayctl.cpp - WPF Overlay 子进程管理器实现
//
// 线程模型: 与 windowctl 相同, 看护线程 WaitForSingleObject 阻塞等子进程退出，
//   不占游戏主线程。CreateProcess 在 boot 线程里一次完成。
// 执行环境: 只调用 CreateProcess / CreateJobObject / 环境变量，不改游戏窗口状态。

#include "overlay/overlayctl.h"
#include "platform/loader.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <string>
#include <filesystem>
#include <vector>

namespace fs = std::filesystem;

#pragma comment(lib, "ws2_32.lib")

namespace
{

volatile LONG g_started = 0;
HANDLE g_process = nullptr;
HANDLE g_job = nullptr;
std::wstring g_exe_path;
std::wstring g_cmdline;
int g_port = 0;
int g_restarts = 0;

std::wstring quote_arg(const std::wstring& s)
{
    // CreateProcessW 命令行：含空格要加双引号
    if (s.find(L' ') == std::wstring::npos && s.find(L'\t') == std::wstring::npos) return s;
    return L"\"" + s + L"\"";
}

DWORD WINAPI watchdog_thread(LPVOID)
{
    for (;;)
    {
        if (g_process == nullptr) break;
        DWORD rc = WaitForSingleObject(g_process, INFINITE);
        if (rc != WAIT_OBJECT_0) break;
        DWORD code = 0;
        GetExitCodeProcess(g_process, &code);
        log_line("[overlayctl] WPF Overlay exited with code " + std::to_string(code));
        // 游戏已退出（进程句柄失效）时不再重启
        if (g_started == 0) break;
        if (g_restarts++ >= 3)
        {
            log_line("[overlayctl] WPF Overlay restart budget exhausted, staying down");
            break;
        }
        log_line("[overlayctl] restarting WPF Overlay (" + std::to_string(g_restarts) + "/3)...");
        Sleep(1000);
        // 重新起进程（无状态）
        STARTUPINFOW si{};
        PROCESS_INFORMATION pi{};
        si.cb = sizeof(si);
        std::wstring cmd = quote_arg(g_exe_path) + L" --pid " + std::to_wstring(GetCurrentProcessId()) + L" --port " + std::to_wstring(g_port);
        if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        {
            log_line("[overlayctl] CreateProcess restart failed: " + std::to_string(GetLastError()));
            break;
        }
        g_process = pi.hProcess;
        if (g_job) AssignProcessToJobObject(g_job, pi.hProcess);
        CloseHandle(pi.hThread);
    }
    return 0;
}

} // namespace

std::wstring overlayctl_dir()
{
    return loader_root() + L"\\overlay";
}

void overlayctl_start(const std::wstring& dir)
{
    if (InterlockedExchange(&g_started, 1) != 0) return;

    g_exe_path = dir + L"\\CesiumOverlay.exe";
    {
        std::error_code ec;
        if (!fs::exists(fs::path(g_exe_path), ec))
        {
            log_line(L"[overlayctl] CesiumOverlay.exe not found, overlay channel unavailable: " + short_path(g_exe_path));
            return;
        }
    }

    // 分配临时端口：bind 0 拿空闲端口后立刻关掉，子进程再 bind 同一端口。
    // 这是 windowctl 的同款模式（只是 windowctl 把 socket 常驻在加载器里转发，
    // Overlay 是 Mod->WPF 直连，加载器不需要常驻 socket）。
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
    {
        log_line("[overlayctl] WSAStartup failed, overlay unavailable");
        return;
    }
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET)
    {
        log_line("[overlayctl] UDP socket creation failed, overlay unavailable");
        WSACleanup();
        return;
    }
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons(0);
    if (bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0)
    {
        log_line("[overlayctl] UDP bind failed, overlay unavailable");
        closesocket(s);
        WSACleanup();
        return;
    }
    sockaddr_in bound{};
    int blen = sizeof(bound);
    if (getsockname(s, reinterpret_cast<sockaddr*>(&bound), &blen) != 0)
    {
        log_line("[overlayctl] UDP getsockname failed, overlay unavailable");
        closesocket(s);
        WSACleanup();
        return;
    }
    g_port = ntohs(bound.sin_port);
    closesocket(s);
    WSACleanup();

    if (g_port <= 0)
    {
        log_line("[overlayctl] cannot allocate overlay port, overlay unavailable");
        return;
    }
    SetEnvironmentVariableW(L"CESIUM_OVERLAY_PORT", std::to_wstring(g_port).c_str());
    std::wstring cfg_path = dir + L"\\overlay_config.json";
    SetEnvironmentVariableW(L"CESIUM_OVERLAY_CFG", cfg_path.c_str());
    // 子进程看不到内存里的环境变量（CreateProcess 继承的是调用方当前块，这里已设置好）
    // 同时子进程靠命令行 --port 再 bind 一次，得到同一个端口
    g_cmdline = std::wstring(L"CesiumOverlay --pid ") + std::to_wstring(GetCurrentProcessId()) + L" --port " + std::to_wstring(g_port);

    // 用 Job Object 包子进程，游戏崩/退出时系统直接杀子进程
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (job)
    {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION li{};
        li.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, &li, sizeof(li));
        g_job = job;
    }

    STARTUPINFOW si{};
    PROCESS_INFORMATION pi{};
    si.cb = sizeof(si);
    std::wstring cmd = quote_arg(g_exe_path) + L" --pid " + std::to_wstring(GetCurrentProcessId()) + L" --port " + std::to_wstring(g_port);
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
    {
        log_line(L"[overlayctl] CreateProcess failed (error " + std::to_wstring(GetLastError()) + L"): " + short_path(g_exe_path));
        return;
    }
    g_process = pi.hProcess;
    if (g_job) AssignProcessToJobObject(g_job, pi.hProcess);
    CloseHandle(pi.hThread);

    log_line("[overlayctl] CesiumOverlay pid " + std::to_string(GetProcessId(pi.hProcess)) + " started · 127.0.0.1:" + std::to_string(g_port) + " · pid=" + std::to_string(GetCurrentProcessId()));

    HANDLE h = CreateThread(nullptr, 0, watchdog_thread, nullptr, 0, nullptr);
    if (h) CloseHandle(h);
}

void overlayctl_shutdown()
{
    InterlockedExchange(&g_started, 0);
    if (g_job)
    {
        CloseHandle(g_job);
        g_job = nullptr;
    }
    if (g_process)
    {
        // Job 已确保子进程被杀，这里不必再 Terminate
        CloseHandle(g_process);
        g_process = nullptr;
    }
}

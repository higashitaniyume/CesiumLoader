// audioctl.cpp - 原生音频控制通道实现
#include "audio/audioctl.h"

#include "config/config.h"
#include "platform/loader.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <mmsystem.h>

#include <string>
#include <vector>
#include <sstream>
#include <cwchar>
#include <cstring>
#include <atomic>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "winmm.lib")

namespace
{

const wchar_t* kRequestName = L"request.txt";
const wchar_t* kStateName = L"state.txt";
const wchar_t* kPortName = L"port.txt";

const int kMaxDirs = 2;
std::wstring g_dirs[kMaxDirs];
int g_dir_count = 0;

volatile LONG g_started = 0;
std::atomic<bool> g_running{ false };
HANDLE g_worker_thread = NULL;
SOCKET g_socket = INVALID_SOCKET;
int g_bound_port = 0;

std::atomic<int> g_sfx_counter{ 0 };
const int kSfxSlotCount = 8;

struct SfxSlot
{
    std::wstring path;
    int last_vol = -1;
};

SfxSlot g_sfx_slots[kSfxSlotCount];
std::wstring g_voice_path;
int g_voice_vol = -1;

HANDLE g_stop_event = NULL;
WSAEVENT g_socket_event = WSA_INVALID_EVENT;
HANDLE g_dir_events[kMaxDirs] = { NULL, NULL };

std::wstring local_appdata_mirror()
{
    wchar_t buf[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return std::wstring();
    return std::wstring(buf) + L"\\AstralParty_ModLoader\\audio";
}

std::wstring path_of(const std::wstring& dir, const wchar_t* name)
{
    return dir + L"\\" + name;
}

bool ensure_dir(const std::wstring& dir)
{
    if (dir.empty()) return false;

    DWORD attr = GetFileAttributesW(dir.c_str());
    if (attr != INVALID_FILE_ATTRIBUTES) return (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;

    size_t slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos && slash > 2)
    {
        if (!ensure_dir(dir.substr(0, slash))) return false;
    }
    return CreateDirectoryW(dir.c_str(), nullptr) != 0;
}

bool read_text(const std::wstring& path, std::string& out)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart > 16384) { CloseHandle(h); return false; }

    std::string buf((size_t)size.QuadPart, '\0');
    DWORD read = 0;
    BOOL ok = TRUE;
    if (!buf.empty())
        ok = ReadFile(h, &buf[0], (DWORD)buf.size(), &read, nullptr);
    CloseHandle(h);
    if (!ok) return false;

    buf.resize(read);
    out.swap(buf);
    return true;
}

bool write_text(const std::wstring& path, const std::string& text)
{
    std::wstring tmp = path + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    DWORD written = 0;
    BOOL ok = TRUE;
    if (!text.empty()) ok = WriteFile(h, text.data(), (DWORD)text.size(), &written, nullptr);
    if (ok && !text.empty()) ok = (written == text.size());
    if (ok) ok = FlushFileBuffers(h);
    CloseHandle(h);

    if (!ok) { DeleteFileW(tmp.c_str()); return false; }
    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

std::wstring utf8_to_wide(const std::string& text)
{
    if (text.empty()) return std::wstring();

    int need = MultiByteToWideChar(CP_UTF8, 0, text.c_str(),
                                   static_cast<int>(text.size()), nullptr, 0);
    if (need <= 0) return std::wstring();

    std::wstring out(static_cast<size_t>(need), L'\0');
    int got = MultiByteToWideChar(CP_UTF8, 0, text.c_str(),
                                  static_cast<int>(text.size()), &out[0], need);
    if (got <= 0) return std::wstring();

    out.resize(static_cast<size_t>(got));
    return out;
}

std::vector<std::string> split(const std::string& s, char delim)
{
    std::vector<std::string> elems;
    std::stringstream ss(s);
    std::string item;
    while (std::getline(ss, item, delim))
    {
        elems.push_back(item);
    }
    return elems;
}

void write_state_and_port(int port)
{
    std::string state;
    state += "version="; state += cesium::config::LoaderConfig::loaderVersion; state += "\r\n";
    state += "available=1\r\n";
    state += "port=" + std::to_string(port) + "\r\n";
    state += "audio=mci,wav,mp3\r\n";

    std::string port_str = std::to_string(port) + "\r\n";

    for (int i = 0; i < g_dir_count; i++)
    {
        write_text(path_of(g_dirs[i], kPortName), port_str);
        write_text(path_of(g_dirs[i], kStateName), state);
    }
}

// -----------------------------------------------------------------------------
// MCI 播放逻辑
// -----------------------------------------------------------------------------

void execute_sfx(const std::wstring& wide_path, int vol)
{
    // 1. 查找是否已有槽位加载了该文件，若已加载则直接复用槽位 play from 0，避免重复 open 导致卡顿
    int slot = -1;
    for (int i = 0; i < kSfxSlotCount; i++)
    {
        if (!g_sfx_slots[i].path.empty() && g_sfx_slots[i].path == wide_path)
        {
            slot = i;
            break;
        }
    }

    if (slot >= 0)
    {
        std::wstring alias = L"CesiumSfx" + std::to_wstring(slot);
        if (vol >= 0 && vol <= 1000 && vol != g_sfx_slots[slot].last_vol)
        {
            std::wstring vol_cmd = L"setaudio " + alias + L" volume to " + std::to_wstring(vol);
            mciSendStringW(vol_cmd.c_str(), NULL, 0, NULL);
            g_sfx_slots[slot].last_vol = vol;
        }
        std::wstring play_cmd = L"play " + alias + L" from 0";
        mciSendStringW(play_cmd.c_str(), NULL, 0, NULL);
        return;
    }

    // 2. 未加载过: 轮转分配新槽位并 open
    slot = (g_sfx_counter.fetch_add(1) & 0x7FFFFFFF) % kSfxSlotCount;
    std::wstring alias = L"CesiumSfx" + std::to_wstring(slot);

    mciSendStringW((L"close " + alias).c_str(), NULL, 0, NULL);
    g_sfx_slots[slot].path.clear();
    g_sfx_slots[slot].last_vol = -1;

    std::wstring open_cmd = L"open \"" + wide_path + L"\" type waveaudio alias " + alias;
    MCIERROR err = mciSendStringW(open_cmd.c_str(), NULL, 0, NULL);
    if (err != 0)
    {
        // 尝试让 MCI 自动识别格式（例如 MP3）
        open_cmd = L"open \"" + wide_path + L"\" alias " + alias;
        err = mciSendStringW(open_cmd.c_str(), NULL, 0, NULL);
    }

    if (err != 0)
    {
        // MCI 解码失败兜底：使用底层 PlaySoundW 异步播放
        PlaySoundW(wide_path.c_str(), NULL, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        return;
    }

    g_sfx_slots[slot].path = wide_path;

    if (vol >= 0 && vol <= 1000)
    {
        std::wstring vol_cmd = L"setaudio " + alias + L" volume to " + std::to_wstring(vol);
        mciSendStringW(vol_cmd.c_str(), NULL, 0, NULL);
        g_sfx_slots[slot].last_vol = vol;
    }

    std::wstring play_cmd = L"play " + alias + L" from 0";
    mciSendStringW(play_cmd.c_str(), NULL, 0, NULL);
}

void execute_voice(const std::wstring& wide_path, int vol)
{
    std::wstring alias = L"CesiumVoice";
    if (g_voice_path == wide_path && !g_voice_path.empty())
    {
        if (vol >= 0 && vol <= 1000 && vol != g_voice_vol)
        {
            std::wstring vol_cmd = L"setaudio " + alias + L" volume to " + std::to_wstring(vol);
            mciSendStringW(vol_cmd.c_str(), NULL, 0, NULL);
            g_voice_vol = vol;
        }
        std::wstring play_cmd = L"play " + alias + L" from 0";
        mciSendStringW(play_cmd.c_str(), NULL, 0, NULL);
        return;
    }

    mciSendStringW((L"close " + alias).c_str(), NULL, 0, NULL);
    g_voice_path.clear();
    g_voice_vol = -1;

    std::wstring open_cmd = L"open \"" + wide_path + L"\" type waveaudio alias " + alias;
    MCIERROR err = mciSendStringW(open_cmd.c_str(), NULL, 0, NULL);
    if (err != 0)
    {
        open_cmd = L"open \"" + wide_path + L"\" alias " + alias;
        err = mciSendStringW(open_cmd.c_str(), NULL, 0, NULL);
    }

    if (err != 0)
    {
        PlaySoundW(wide_path.c_str(), NULL, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        return;
    }

    g_voice_path = wide_path;

    if (vol >= 0 && vol <= 1000)
    {
        std::wstring vol_cmd = L"setaudio " + alias + L" volume to " + std::to_wstring(vol);
        mciSendStringW(vol_cmd.c_str(), NULL, 0, NULL);
        g_voice_vol = vol;
    }

    std::wstring play_cmd = L"play " + alias + L" from 0";
    mciSendStringW(play_cmd.c_str(), NULL, 0, NULL);
}

void execute_bgm_play(const std::wstring& wide_path, int vol, bool loop)
{
    std::wstring alias = L"CesiumBgm";
    mciSendStringW((L"close " + alias).c_str(), NULL, 0, NULL);

    std::wstring open_cmd = L"open \"" + wide_path + L"\" alias " + alias;
    MCIERROR err = mciSendStringW(open_cmd.c_str(), NULL, 0, NULL);
    if (err != 0)
    {
        open_cmd = L"open \"" + wide_path + L"\" type waveaudio alias " + alias;
        err = mciSendStringW(open_cmd.c_str(), NULL, 0, NULL);
    }

    if (err != 0) return;

    if (vol >= 0 && vol <= 1000)
    {
        std::wstring vol_cmd = L"setaudio " + alias + L" volume to " + std::to_wstring(vol);
        mciSendStringW(vol_cmd.c_str(), NULL, 0, NULL);
    }

    std::wstring play_cmd = loop ? (L"play " + alias + L" repeat") : (L"play " + alias + L" from 0");
    mciSendStringW(play_cmd.c_str(), NULL, 0, NULL);
}

void execute_bgm_stop()
{
    mciSendStringW(L"stop CesiumBgm", NULL, 0, NULL);
    mciSendStringW(L"close CesiumBgm", NULL, 0, NULL);
}

void execute_bgm_pause()
{
    mciSendStringW(L"pause CesiumBgm", NULL, 0, NULL);
}

void execute_bgm_resume()
{
    mciSendStringW(L"resume CesiumBgm", NULL, 0, NULL);
}

void execute_bgm_volume(int vol)
{
    if (vol >= 0 && vol <= 1000)
    {
        std::wstring vol_cmd = L"setaudio CesiumBgm volume to " + std::to_wstring(vol);
        mciSendStringW(vol_cmd.c_str(), NULL, 0, NULL);
    }
}

void execute_stop(const std::string& channel)
{
    if (channel == "voice")
    {
        mciSendStringW(L"stop CesiumVoice", NULL, 0, NULL);
        mciSendStringW(L"close CesiumVoice", NULL, 0, NULL);
        g_voice_path.clear();
        g_voice_vol = -1;
    }
    else if (channel == "sfx")
    {
        for (int i = 0; i < kSfxSlotCount; i++)
        {
            std::wstring alias = L"CesiumSfx" + std::to_wstring(i);
            mciSendStringW((L"stop " + alias).c_str(), NULL, 0, NULL);
            mciSendStringW((L"close " + alias).c_str(), NULL, 0, NULL);
            g_sfx_slots[i].path.clear();
            g_sfx_slots[i].last_vol = -1;
        }
    }
    else if (channel == "bgm")
    {
        execute_bgm_stop();
    }
}

void handle_command(const std::string& raw)
{
    if (raw.empty()) return;

    // 清除换行回车
    std::string line = raw;
    while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' '))
    {
        line.pop_back();
    }
    if (line.empty()) return;

    auto parts = split(line, '|');
    if (parts.empty()) return;

    const std::string& op = parts[0];

    if (op == "PLAY" && parts.size() >= 4)
    {
        // PLAY|<channel>|<volume 0-1000>|<path>
        const std::string& channel = parts[1];
        int vol = std::atoi(parts[2].c_str());
        std::wstring wide_path = utf8_to_wide(parts[3]);

        if (channel == "voice")
        {
            execute_voice(wide_path, vol);
        }
        else
        {
            execute_sfx(wide_path, vol);
        }
    }
    else if (op == "BGM_PLAY" && parts.size() >= 4)
    {
        // BGM_PLAY|<volume 0-1000>|<loop 0/1>|<path>
        int vol = std::atoi(parts[1].c_str());
        bool loop = (parts[2] == "1" || parts[2] == "true");
        std::wstring wide_path = utf8_to_wide(parts[3]);
        execute_bgm_play(wide_path, vol, loop);
    }
    else if (op == "BGM_STOP")
    {
        execute_bgm_stop();
    }
    else if (op == "BGM_PAUSE")
    {
        execute_bgm_pause();
    }
    else if (op == "BGM_RESUME")
    {
        execute_bgm_resume();
    }
    else if (op == "BGM_VOL" && parts.size() >= 2)
    {
        int vol = std::atoi(parts[1].c_str());
        execute_bgm_volume(vol);
    }
    else if (op == "STOP" && parts.size() >= 2)
    {
        execute_stop(parts[1]);
    }
}

// -----------------------------------------------------------------------------
// 工作线程
// -----------------------------------------------------------------------------

DWORD WINAPI audioctl_worker(LPVOID)
{
    std::string last_request[kMaxDirs];

    // 初始化已有的 request.txt 避免执行历史命令
    for (int i = 0; i < g_dir_count; i++)
    {
        read_text(path_of(g_dirs[i], kRequestName), last_request[i]);
    }

    char recv_buf[4096];
    int timeout_ticks = 0;
    const int kWatchdogTicks = 10;   // 10 × 500ms 超时 = 约 5 秒兜底重读一次

    while (g_running.load())
    {
        HANDLE wait_handles[1 + 1 + kMaxDirs];
        int handle_count = 0;

        wait_handles[handle_count++] = g_stop_event;

        if (g_socket_event != WSA_INVALID_EVENT && g_socket_event != NULL)
        {
            wait_handles[handle_count++] = g_socket_event;
        }

        // 目录句柄 -> wait_handles 下标的映射。不能用 "dir_start_idx + i" 推算:
        // 某个目录的通知句柄创建失败(NULL/INVALID)时数组是压缩的, 推算会整体错一位,
        // 结果是"信号被算到别的目录头上"、真正有变更的目录永远读不到。
        int dir_wait_idx[kMaxDirs];
        for (int i = 0; i < kMaxDirs; i++) dir_wait_idx[i] = -1;
        for (int i = 0; i < g_dir_count; i++)
        {
            if (g_dir_events[i] != NULL && g_dir_events[i] != INVALID_HANDLE_VALUE)
            {
                dir_wait_idx[i] = handle_count;
                wait_handles[handle_count++] = g_dir_events[i];
            }
        }

        DWORD wait_res = WaitForMultipleObjects(handle_count, wait_handles, FALSE, 500);

        if (!g_running.load()) break;

        // 1. 尝试从 UDP 套接字非阻塞/超时接收数据
        if (g_socket != INVALID_SOCKET)
        {
            for (;;)
            {
                sockaddr_in client_addr{};
                int client_len = sizeof(client_addr);
                int n = recvfrom(g_socket, recv_buf, sizeof(recv_buf) - 1, 0,
                                 (sockaddr*)&client_addr, &client_len);
                if (n > 0)
                {
                    recv_buf[n] = '\0';
                    handle_command(std::string(recv_buf, n));
                }
                else
                {
                    break;
                }
            }
            if (g_socket_event != WSA_INVALID_EVENT && g_socket_event != NULL)
            {
                WSAResetEvent(g_socket_event);
            }
        }

        // 2. 检查目录变更通知（仅当目录发生变更时才读取文件，避免空闲轮询大量打开文件）
        //    看门狗: 每 10 次超时(=约 5 秒)兜底重读一次, 防止通知事件丢失(目录句柄失效/通知被吞)
        //    导致文件通道永久静默; 频率远低于改造前的每轮都读。
        bool force_read_all = false;
        if (wait_res == WAIT_TIMEOUT)
        {
            if (++timeout_ticks >= kWatchdogTicks)
            {
                timeout_ticks = 0;
                force_read_all = true;
            }
        }
        else
        {
            timeout_ticks = 0;
        }

        for (int i = 0; i < g_dir_count; i++)
        {
            bool has_event = (dir_wait_idx[i] >= 0);
            bool dir_signaled = force_read_all || !has_event;   // 没有通知句柄时退回"每轮都读", 保证该目录通道仍然可用
            if (has_event && wait_res == (DWORD)(WAIT_OBJECT_0 + dir_wait_idx[i]))
            {
                dir_signaled = true;
                FindNextChangeNotification(g_dir_events[i]);
            }

            if (dir_signaled)
            {
                std::string content;
                if (read_text(path_of(g_dirs[i], kRequestName), content))
                {
                    if (content != last_request[i])
                    {
                        last_request[i] = content;
                        std::stringstream ss(content);
                        std::string line;
                        while (std::getline(ss, line))
                        {
                            handle_command(line);
                        }
                    }
                }
            }
        }
    }

    return 0;
}

} // namespace

std::wstring audioctl_dir()
{
    return loader_root() + L"\\audio";
}

void audioctl_start(const std::wstring& dir)
{
    if (InterlockedExchange(&g_started, 1) != 0) return;

    g_dir_count = 0;
    if (!dir.empty())
    {
        if (ensure_dir(dir)) g_dirs[g_dir_count++] = dir;
    }

    std::wstring mirror = local_appdata_mirror();
    if (!mirror.empty() && mirror != dir)
    {
        if (ensure_dir(mirror)) g_dirs[g_dir_count++] = mirror;
    }

    // 目录变更通知句柄
    for (int i = 0; i < g_dir_count; i++)
    {
        g_dir_events[i] = FindFirstChangeNotificationW(
            g_dirs[i].c_str(),
            FALSE,
            FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE);
    }

    g_stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    // 初始化 Winsock
    WSADATA wsaData{};
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) == 0)
    {
        g_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (g_socket != INVALID_SOCKET)
        {
            sockaddr_in addr{};
            addr.sin_family = AF_INET;
            addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK); // 127.0.0.1
            addr.sin_port = htons(0); // 随机分配空闲端口

            if (bind(g_socket, (sockaddr*)&addr, sizeof(addr)) == 0)
            {
                sockaddr_in bound{};
                int bound_len = sizeof(bound);
                if (getsockname(g_socket, (sockaddr*)&bound, &bound_len) == 0)
                {
                    g_bound_port = ntohs(bound.sin_port);
                }
            }

            g_socket_event = WSACreateEvent();
            if (g_socket_event != WSA_INVALID_EVENT)
            {
                WSAEventSelect(g_socket, g_socket_event, FD_READ | FD_CLOSE);
            }
        }
    }

    if (g_bound_port > 0)
    {
        SetEnvironmentVariableW(L"CESIUM_AUDIO_PORT", std::to_wstring(g_bound_port).c_str());
        write_state_and_port(g_bound_port);
        log_line(L"[audioctl] Native audio service ready on 127.0.0.1:" + std::to_wstring(g_bound_port) +
                 L"  2 dir(s): " + dir + L"  " + mirror);
    }
    else
    {
        write_state_and_port(0);
        log_line(L"[audioctl] Native audio service file-only fallback ready  2 dir(s): " + dir + L"  " + mirror);
    }

    g_running.store(true);
    g_worker_thread = CreateThread(nullptr, 0, audioctl_worker, nullptr, 0, nullptr);
}

void audioctl_stop()
{
    if (!g_running.exchange(false)) return;

    if (g_stop_event != NULL)
    {
        SetEvent(g_stop_event);
    }

    if (g_socket != INVALID_SOCKET)
    {
        closesocket(g_socket);
        g_socket = INVALID_SOCKET;
    }

    if (g_worker_thread != NULL)
    {
        WaitForSingleObject(g_worker_thread, 1000);
        CloseHandle(g_worker_thread);
        g_worker_thread = NULL;
    }

    if (g_socket_event != WSA_INVALID_EVENT && g_socket_event != NULL)
    {
        WSACloseEvent(g_socket_event);
        g_socket_event = WSA_INVALID_EVENT;
    }

    for (int i = 0; i < g_dir_count; i++)
    {
        if (g_dir_events[i] != NULL && g_dir_events[i] != INVALID_HANDLE_VALUE)
        {
            FindCloseChangeNotification(g_dir_events[i]);
            g_dir_events[i] = NULL;
        }
    }

    if (g_stop_event != NULL)
    {
        CloseHandle(g_stop_event);
        g_stop_event = NULL;
    }

    // 关闭所有 MCI 槽位
    for (int i = 0; i < kSfxSlotCount; i++)
    {
        std::wstring alias = L"CesiumSfx" + std::to_wstring(i);
        mciSendStringW((L"close " + alias).c_str(), NULL, 0, NULL);
        g_sfx_slots[i].path.clear();
        g_sfx_slots[i].last_vol = -1;
    }
    mciSendStringW(L"close CesiumVoice", NULL, 0, NULL);
    g_voice_path.clear();
    g_voice_vol = -1;

    mciSendStringW(L"close CesiumBgm", NULL, 0, NULL);

    WSACleanup();
}

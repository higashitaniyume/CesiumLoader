// Process-local UDP control plane. Clock hook readers never take this mutex.
#include <winsock2.h>
#include <ws2tcpip.h>
#include "speed/speedctl.h"
#include "config/config.h"
#include "platform/loader.h"
#include "speed/speedhack.h"
#include <fmt/format.h>
#include <mutex>
#include <map>
#include <cstdint>
#include <cmath>
#include <charconv>

#pragma comment(lib, "ws2_32.lib")

namespace {
constexpr size_t kMaxPacket = 512;
SOCKET g_socket = INVALID_SOCKET;
volatile LONG g_started = 0;
double g_base = 1;
uint64_t g_revision = 0;
std::mutex g_control_mutex;
struct Client { uint64_t last_set = 0; ULONGLONG touched = 0; bool last_ok = false; };
std::map<unsigned short, Client> g_clients;

bool parse_id(const std::string& text, uint64_t& id)
{
    if (text.empty() || text.size() > 19) return false;
    id = 0;
    for (char c : text) {
        if (c < '0' || c > '9') return false;
        const auto next = id * 10 + c - '0';
        if (next < id || next > INT64_MAX) return false;
        id = next;
    }
    return id != 0;
}

bool parse_speed(const std::string& text, double& speed)
{
    if (text.empty() || text.size() > 16 || text.front() < '0' || text.front() > '9' || text.back() == '.') return false;
    bool dot = false;
    for (char c : text) {
        if (c == '.' && !dot) { dot = true; continue; }
        if (c < '0' || c > '9') return false;
    }
    const auto result = std::from_chars(text.data(), text.data() + text.size(), speed);
    return result.ec == std::errc() && result.ptr == text.data() + text.size() && speed >= kSpeedMin && speed <= kSpeedMax;
}

// Caller holds the control-plane mutex, so a response cannot mix different setters.
std::string state(uint64_t id, bool ok)
{
    return fmt::format("protocol=1\nid={}\nrevision={}\nok={}\nversion={}\nspeed={:.3f}\nbase={:.3f}\nactive={}\nhooks={}\n",
        id, g_revision, ok ? 1 : 0, cesium::config::LoaderConfig::loaderVersion,
        speedhack_get_speed(), g_base, speedhack_active() ? 1 : 0, speedhack_hook_count());
}

DWORD WINAPI listen_thread(LPVOID)
{
    char buffer[kMaxPacket];
    for (;;) {
        sockaddr_in client{};
        int length = sizeof(client);
        const int n = recvfrom(g_socket, buffer, sizeof(buffer), 0, reinterpret_cast<sockaddr*>(&client), &length);
        if (n <= 0 || n >= static_cast<int>(sizeof(buffer)) || client.sin_addr.s_addr != htonl(INADDR_LOOPBACK)) continue;
        const std::string text(buffer, n);
        const bool set = text.compare(0, 4, "set ") == 0;
        if (!set && text.compare(0, 4, "get ") != 0) continue;
        const auto split = text.find(' ', 4);
        uint64_t id;
        if (!parse_id(text.substr(4, set ? split - 4 : std::string::npos), id)) continue;
        uint64_t reply_id = id;
        double speed = 0;
        if (set) {
            if (split == std::string::npos) continue;
            const auto attempt_split = text.find(' ', split + 1);
            if (!parse_speed(text.substr(split + 1, attempt_split == std::string::npos ? std::string::npos : attempt_split - split - 1), speed)) continue;
            if (attempt_split != std::string::npos && !parse_id(text.substr(attempt_split + 1), reply_id)) continue;
        }

        std::string response;
        {
            std::lock_guard<std::mutex> guard(g_control_mutex);
            bool ok = true;
            if (set) {
                // Bounded endpoint history: retries are idempotent and reordered
                // setters from the same SDK socket cannot restore an older speed.
                if (g_clients.find(client.sin_port) == g_clients.end() && g_clients.size() >= 64) {
                    auto oldest = g_clients.begin();
                    for (auto it = g_clients.begin(); it != g_clients.end(); ++it)
                        if (it->second.touched < oldest->second.touched) oldest = it;
                    g_clients.erase(oldest);
                }
                auto& history = g_clients[client.sin_port];
                history.touched = GetTickCount64();
                if (id > history.last_set) {
                    history.last_set = id;
                    ok = speedhack_set_speed(speed);
                    history.last_ok = ok;
                    if (ok) ++g_revision;
                } else ok = id == history.last_set && history.last_ok;
            }
            response = state(reply_id, ok);
        }
        sendto(g_socket, response.data(), static_cast<int>(response.size()), 0, reinterpret_cast<sockaddr*>(&client), length);
    }
}
} // namespace

bool speedctl_set_speed(double speed)
{
    std::lock_guard<std::mutex> guard(g_control_mutex);
    const bool applied = speedhack_set_speed(speed);
    if (applied) ++g_revision;
    return applied;
}

std::wstring speedctl_dir() { return loader_root() + L"\\speed"; } // Legacy API only; no files.

void speedctl_start(const std::wstring& dir, double base_speed)
{
    (void)dir;
    if (InterlockedExchange(&g_started, 1) != 0) return;
    SetEnvironmentVariableW(L"CESIUM_SPEED_PORT", nullptr);
    g_base = std::isfinite(base_speed) && base_speed >= kSpeedMin && base_speed <= kSpeedMax ? base_speed : 1;
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) { log_line("[speedctl] WSAStartup failed"); return; }
    g_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (g_socket != INVALID_SOCKET) {
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        if (bind(g_socket, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0) {
            int length = sizeof(address);
            if (getsockname(g_socket, reinterpret_cast<sockaddr*>(&address), &length) == 0) {
                HANDLE thread = CreateThread(nullptr, 0, listen_thread, nullptr, 0, nullptr);
                if (thread) {
                    CloseHandle(thread);
                    const auto port = std::to_wstring(ntohs(address.sin_port));
                    SetEnvironmentVariableW(L"CESIUM_SPEED_PORT", port.c_str());
                    log_line(L"[speedctl] UDP channel ready · 127.0.0.1:" + port);
                    return;
                }
            }
        }
        closesocket(g_socket);
        g_socket = INVALID_SOCKET;
    }
    WSACleanup();
    log_line("[speedctl] UDP startup failed, speed control unavailable");
}

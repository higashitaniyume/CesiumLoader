// Real loopback transport with a fake clock engine; no game required.
#include <winsock2.h>
#include <ws2tcpip.h>
#include "platform/loader.h"
#include "platform/build_info.h"
#include "speed/speedctl.h"
#include "speed/speedhack.h"
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <vector>
#include <filesystem>
#pragma comment(lib, "ws2_32.lib")

namespace {
std::wstring root;
std::atomic<double> actual{4};
std::atomic<int> writers{0}, overlaps{0}, applies{0};
std::atomic<bool> available{true};
void check(bool condition, const char* message) {
    if (!condition) { std::fprintf(stderr, "FAIL: %s\n", message); std::exit(1); }
}
SOCKET client(int port, DWORD timeout = 150) {
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    check(s != INVALID_SOCKET, "client socket");
    sockaddr_in addr{}; addr.sin_family = AF_INET; addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK); addr.sin_port = htons(port);
    check(connect(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0, "connect");
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    return s;
}
std::string transact(SOCKET s, const std::string& text) {
    check(send(s, text.data(), static_cast<int>(text.size()), 0) == text.size(), "send");
    char buf[2048]; int n = recv(s, buf, sizeof(buf), 0);
    return n > 0 ? std::string(buf, n) : "";
}
void expect(SOCKET s, const std::string& text, const std::string& field) {
    check(transact(s, text).find(field + "\n") != std::string::npos, text.c_str());
}
}
void log_line(const char*) {}
void log_line(const std::string&) {}
void log_line(const std::wstring&) {}
std::wstring loader_root() { return root; }
bool speedhack_active() { return available; }
int speedhack_hook_count() { return available ? 4 : 0; }
double speedhack_get_speed() { return actual.load(); }
bool speedhack_set_speed(double speed) {
    if (!(speed >= 1 && speed <= 100) || !available) return false;
    if (writers.fetch_add(1) != 0) overlaps++;
    Sleep(1); actual = speed; applies++; writers--;
    return true;
}
int main() {
    wchar_t temp[MAX_PATH]; check(GetTempPathW(MAX_PATH, temp) > 0, "temporary directory");
    root = std::wstring(temp) + L"cesium-speedctl-" + std::to_wstring(GetCurrentProcessId());
    check(!std::filesystem::exists(root), "isolated nonexistent root");
    speedctl_start(root + L"\\speed", 2);
    wchar_t portText[16]{};
    check(GetEnvironmentVariableW(L"CESIUM_SPEED_PORT", portText, 16) > 0, "port exported synchronously");
    int port = _wtoi(portText); check(port > 0 && port <= 65535, "valid ephemeral port");
    SOCKET a = client(port), b = client(port);
    expect(a, "get 1", "speed=4.000"); expect(a, "get 2", "base=2.000");
    expect(a, "set 3 1.000", "speed=1.000");
    check(speedctl_set_speed(4), "external setter");
    const int beforeRetry = applies;
    expect(a, "set 3 1.000", "speed=4.000"); // duplicate cannot undo an external change
    check(applies == beforeRetry, "idempotent retry");
    expect(a, "set 4 2.500", "speed=2.500");
    expect(a, "set 3 1.000", "ok=0"); check(actual == 2.5, "reordered mutation ignored");
    expect(b, "set 1 3.000", "speed=3.000"); // client ids have independent histories
    expect(a, "get 5", "speed=3.000");
    for (const char* invalid : {"set 6 0.5", "set 6 nan", "set 6 1e1", "set 6 0x2", "set 6 2,5", "set 6 101", "set 6 1.",
                                 "set -1 1", "set 0 1", "set 9223372036854775808 1", "get 7 junk", "get 1\nset 8 9"}) {
        check(transact(a, invalid).empty(), "malformed request ignored"); check(actual == 3, "malformed request preserves engine");
    }
    check(transact(a, std::string("set 6 1\0junk", 12)).empty(), "embedded NUL rejected");
    check(transact(a, std::string(2000, 'x')).empty(), "oversized packet ignored");
    expect(a, "get 7", "speed=3.000");
    available = false; expect(a, "set 8 1", "ok=0"); available = true;
    expect(a, "set 8 1 80", "ok=0"); check(actual == 3, "failed mutation retry remains rejected");
    expect(a, "set 9 1", "ok=1");
    std::vector<std::thread> threads;
    for (int i = 0; i < 6; i++) threads.emplace_back([i] {
        for (int j = 0; j < 10; j++) check(speedctl_set_speed(2 + i), "concurrent setter");
    });
    threads.emplace_back([port] {
        // Fake setters deliberately sleep while holding the control lock; allow
        // for all competing writers and Windows timer granularity, not UDP latency.
        SOCKET c = client(port, 2000);
        for (int i = 1; i <= 20; i++) expect(c, "set " + std::to_string(i) + " 2.000", "ok=1");
        closesocket(c);
    });
    for (auto& thread : threads) thread.join();
    check(overlaps == 0, "all writers serialized");
    expect(a, "get 10", "speed=" + std::to_string(static_cast<int>(actual.load())) + ".000");
    expect(a, "set 11 2.000 100", "id=100");
    const int beforeAttempt = applies;
    expect(a, "set 11 2.000 101", "id=101");
    check(applies == beforeAttempt, "retry attempt ids do not reapply mutation");
    const double before = actual;
    check(!speedctl_set_speed(.5), "slowdown rejected"); check(actual == before, "rejected setter unchanged");
    check(!std::filesystem::exists(root), "UDP channel creates no directories or files");
    check(cesium::console_title().find(L"CesiumLoader v") == 0, "console version prefix");
    check(cesium::console_title().find(L" (unknown)") != std::wstring::npos, "non-build fallback hash");
    check(cesium::console_title(true).find(L"Debug Shell") != std::wstring::npos, "shell preserves versioned title");
    closesocket(a); closesocket(b);
    std::puts("speedctl UDP tests passed");
}

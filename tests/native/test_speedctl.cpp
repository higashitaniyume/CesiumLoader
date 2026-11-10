// Exercise the real file channel against an isolated directory and a fake engine.
#include "platform/loader.h"
#include "speed/speedctl.h"
#include "speed/speedhack.h"
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <thread>
#include <vector>

namespace {
std::wstring root;
std::atomic<double> actual{4};
std::atomic<int> writers{0}, overlaps{0}, applies{0};
void check(bool condition, const char* message) {
    if (!condition) { std::fprintf(stderr, "FAIL: %s\n", message); std::exit(1); }
}
std::string read(const std::wstring& path) {
    std::ifstream file{std::filesystem::path(path)};
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
void request(const char* text) {
    const auto path = root + L"\\primary\\request.txt";
    const auto tmp = path + L".new";
    { std::ofstream file{std::filesystem::path(tmp)}; file << text; }
    check(MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING) != 0, "replace request");
}
bool wait_speed(double speed) {
    const auto start = GetTickCount64();
    while (GetTickCount64() - start < 3000) {
        if (actual.load() == speed && read(root + L"\\primary\\state.txt").find("speed=" + std::to_string((int)speed) + ".000") != std::string::npos) return true;
        Sleep(10);
    }
    return false;
}
}

void log_line(const char*) {}
void log_line(const std::string&) {}
void log_line(const std::wstring&) {}
std::wstring loader_root() { return root; }
std::wstring short_path(const std::wstring& path) { return path; }
bool speedhack_active() { return true; }
int speedhack_hook_count() { return 4; }
double speedhack_get_speed() { return actual.load(); }
bool speedhack_set_speed(double speed) {
    if (!(speed >= 1 && speed <= 100)) return false;
    if (writers.fetch_add(1) != 0) overlaps++;
    Sleep(1);
    actual = speed; applies++;
    writers--;
    return true;
}

int main() {
    wchar_t temp[MAX_PATH];
    check(GetTempPathW(MAX_PATH, temp) > 0, "temporary directory");
    root = std::wstring(temp) + L"cesium-speedctl-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    check(SetEnvironmentVariableW(L"LOCALAPPDATA", root.c_str()) != 0, "isolate mirror");
    speedctl_start(root + L"\\primary", 1);
    // Issue immediately: the watch thread must not swallow the first mod request.
    request("1.000\r\n");
    check(wait_speed(1), "first request acknowledged");
    check(speedctl_set_speed(4), "external setter");
    check(wait_speed(4), "external setter publishes actual speed");
    check(read(root + L"\\AstralParty_ModLoader\\speed\\state.txt").find("speed=4.000") != std::string::npos, "mirror updated");
    request("1.000\r\n"); // same bytes as the previous request, new file generation
    check(wait_speed(1), "identical reset request is not lost after external speed change");

    std::vector<std::thread> threads;
    for (int i = 0; i < 6; i++) threads.emplace_back([i] {
        for (int j = 0; j < 10; j++) check(speedctl_set_speed(2 + i), "concurrent setter");
    });
    for (auto& thread : threads) thread.join();
    check(overlaps == 0, "engine writers serialized");
    check(wait_speed(actual.load()), "publication matches last writer");

    const auto state = root + L"\\primary\\state.txt";
    HANDLE held = CreateFileW(state.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    check(held != INVALID_HANDLE_VALUE, "hold acknowledgement against deletion");
    const double before = actual.load();
    check(!speedctl_set_speed(9), "refuse change if old acknowledgement cannot be invalidated");
    check(actual.load() == before, "failed invalidation leaves engine untouched");
    CloseHandle(held);
    check(!speedctl_set_speed(0.5), "reject slowdown");
    check(actual.load() == before, "invalid speed leaves engine untouched");
    check(wait_speed(before), "invalid request republishes actual state");
    // The watch thread is process-lifetime by design. Test artifacts are isolated;
    // removing only this test's generated root is safe while it waits for files.
    std::filesystem::remove_all(root);
    std::puts("speedctl tests passed");
    return 0;
}

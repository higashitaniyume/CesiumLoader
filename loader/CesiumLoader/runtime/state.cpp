// state.cpp - 运行期状态快照实现(见 state.h)

#include "runtime/state.h"

#include <mutex>

namespace
{

struct State
{
    cesium::runtime::Snapshot snap;
    bool forward = true;
};

std::mutex g_mutex;
State g_state;

} // namespace

namespace cesium::runtime
{

void mark_started()
{
    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    const ULONGLONG ticks = (static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    std::lock_guard<std::mutex> lock(g_mutex);
    // 100ns -> ms
    g_state.snap.started_utc_ms = ticks / 10000ULL;
}

Snapshot snapshot()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_state.snap;
}

LoaderConfig config()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_state.snap.cfg;
}

void set_config(const LoaderConfig& cfg)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_state.snap.cfg = cfg;
}

void set_mods(std::vector<ModStatus> mods)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_state.snap.mods = std::move(mods);
}

void set_versions(const std::string& game_version, const std::string& unity_version)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_state.snap.game_version = game_version;
    g_state.snap.unity_version = unity_version;
}

void set_exe_path(const std::wstring& exe_path)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_state.snap.exe_path = exe_path;
}

bool forward_enabled()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_state.forward;
}

void set_forward_enabled(bool on)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_state.forward = on;
}

} // namespace cesium::runtime

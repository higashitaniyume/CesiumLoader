// update.cpp - 加载器更新检查(接口见 update.h)
//
// 数据流(一张图):
//
//   [引导线程] start()
//        │  ① 冷却期内? ──是──> 复用 <loader>\update\state.json 的缓存, 只打一行日志
//        │  ② 否: 起后台线程 ──> WinHTTP GET version.json ──> 解析 ──> 比较版本
//        │                                                        │
//        │                     ┌──────────────────────────────────┘
//        │                     ├─> 原子写 state.json(给 mod / 排查用)
//        │                     ├─> 写 manifest.json(最后一次成功拿到的原文)
//        │                     └─> 控制台/日志一行 [update] …
//        └─ ③ 同时起 UDP IPC(端口 → CESIUM_UPDATE_PORT), 供 mod 请求强制检查/打开浏览器
//
// 状态文件是**唯一**的交接面: 原生层只写不读(除冷却判断), mod 只读不写。
// 这样 mod 崩溃或没加载都不影响检查, 检查失败也不会把 UI 拖垮。
//
// 为什么用 WinHTTP 而不是 WinINet: WinINet 依赖用户会话与 IE 设置, 在注入进程里
// 容易出现代理/缓存相关的怪行为; WinHTTP 是服务级 HTTP 栈, 无会话依赖。
// 全部函数走 LoadLibrary + GetProcAddress 动态解析 —— 不往 version.dll 的导入表里
// 加 winhttp.dll: 万一目标机器缺这个 DLL(极旧/精简系统), 缺导入会让整个加载器
// 加载失败、游戏直接起不来; 动态解析只让"检查更新"这一个功能不可用。

#include "update/update.h"

#include "config/jsonc.h"
#include "logging/logging.h"
#include "platform/loader.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>   // ShellExecuteW
#include <winhttp.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "shell32.lib")

namespace
{

// ---------- 常量 ----------

// 清单只有一个版本号与几个链接, 正常 1 KB 以内。给到 512 KB 是防御:
// 被导向到错误地址(比如登录页/报错页)时, 别把几 MB HTML 读进内存。
constexpr size_t kMaxManifestBytes = 512 * 1024;

// 状态文件读取上限(同样是防御; 正常 < 4 KB)。
constexpr size_t kMaxStateBytes = 64 * 1024;

constexpr int kConnectTimeoutMs = 5000;
constexpr int kDefaultTimeoutSec = 8;
constexpr unsigned kDefaultIntervalHours = 6;

// 默认镜像前缀: 与 AstralParty.Toys 的 GitHubMirrorHelper 保持一致(用户在国内直连
// GitHub 经常超时, 直连失败后自动换这个前缀再试一次)。
constexpr char kDefaultMirrorPrefix[] = "https://gh-proxy.com/";

constexpr char kDefaultManifestUrl[] =
    "https://github.com/higashitaniyume/CesiumLoader/releases/latest/download/version.json";

constexpr char kRepo[] = "higashitaniyume/CesiumLoader";

// ---------- 配置副本 ----------

struct Settings
{
    bool enabled = true;
    std::string url = kDefaultManifestUrl;
    std::string mirror = "auto";          // auto / off / 前缀 URL
    unsigned interval_hours = kDefaultIntervalHours;
    unsigned timeout_sec = kDefaultTimeoutSec;
};

Settings g_settings;
std::wstring g_dir;
std::mutex g_mutex;
bool g_started = false;
std::atomic<bool> g_checking{false};

// ---------- 结果 ----------

struct Result
{
    std::string status;                   // update-available / up-to-date / error / disabled
    std::string latest_version;
    std::string release_tag;
    std::string release_date;
    std::string release_notes;
    std::string release_url;
    std::string download_url;
    std::string download_sha256;
    long long download_size = 0;
    std::string source;                    // direct / mirror / ""
    std::string error;
    unsigned long long checked_at_unix = 0;
};

cesium::update::Summary g_summary;        // g_mutex 保护

std::string current_version()
{
    return LoaderConfig::loaderVersion;
}

void log_update(const std::string& body)
{
    log_line("[update] " + body);
}

// "有可用更新"用亮绿突出: 这是玩家真正需要看到的那一行, 混在一屏灰白日志里很容易错过。
//
// 关键: **不要**用 console_set_color + log_line —— 颜色是控制台全局状态, 引导线程正好在
// 这个窗口里执行 `console_set_color(LOG_DIM); log_line("[loader] subdirs ...")`, 它不拿写锁,
// 于是这一行被染成暗灰(实测: 第一条绿色更新提示就是这样变灰的)。
// console_write_styled 每行显式给色、不碰全局 g_color, 才是并发下正确的做法;
// 文件那一半自己调 log::write, 与 log_line 的落盘格式保持一致(带时间戳)。
void log_update_available(const std::string& body)
{
    cesium::log::write("[update] " + body);
    if (!console_available()) return;

    // 与控制台的 tag 对齐规则一致: 前缀补到 12 列, 续行缩进才对得上。
    std::string prefix = "[update]";
    const int pad = 12 - static_cast<int>(prefix.size());
    if (pad > 0) prefix.append(static_cast<size_t>(pad), ' ');

    console_lock();
    console_write_styled(prefix.c_str(), body.c_str(), LOG_DIM, LOG_GREEN);
    console_unlock();
}

// ---------- 小工具 ----------

std::string trim_copy(const std::string& text)
{
    size_t b = 0, e = text.size();
    while (b < e && (text[b] == ' ' || text[b] == '\t' || text[b] == '\r' || text[b] == '\n')) ++b;
    while (e > b && (text[e - 1] == ' ' || text[e - 1] == '\t' || text[e - 1] == '\r' || text[e - 1] == '\n')) --e;
    return text.substr(b, e - b);
}

std::string lower_ascii(const std::string& text)
{
    std::string out;
    out.reserve(text.size());
    for (char c : text)
        out.push_back(static_cast<char>((c >= 'A' && c <= 'Z') ? c - 'A' + 'a' : c));
    return out;
}

bool starts_with_ci(const std::string& text, const std::string& prefix)
{
    if (text.size() < prefix.size()) return false;
    return _strnicmp(text.c_str(), prefix.c_str(), prefix.size()) == 0;
}

std::wstring widen_utf8(const std::string& text)
{
    if (text.empty()) return std::wstring();
    const int need = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    if (need <= 0) return std::wstring();
    std::wstring out(static_cast<size_t>(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &out[0], need);
    return out;
}

std::string narrow_utf8(const std::wstring& text)
{
    if (text.empty()) return std::string();
    const int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                         nullptr, 0, nullptr, nullptr);
    if (need <= 0) return std::string();
    std::string out(static_cast<size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                        &out[0], need, nullptr, nullptr);
    return out;
}

unsigned long long now_unix()
{
    return static_cast<unsigned long long>(::time(nullptr));
}

std::string iso_utc(unsigned long long unix_seconds)
{
    if (unix_seconds == 0) return std::string();
    __time64_t t = static_cast<__time64_t>(unix_seconds);
    tm utc{};
    if (_gmtime64_s(&utc, &t) != 0) return std::string();
    char buf[32] = {};
    strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return std::string(buf);
}

// 版本比较: 只取前三段数字。非数字后缀(-android-mobile / -beta 之类)不参与比较,
// 这样安卓安装器写进去的 "3.7.5-android-mobile" 不会被当成比 "3.7.5" 新。
void parse_version(const std::string& text, int out[3])
{
    out[0] = out[1] = out[2] = 0;
    size_t i = 0;
    if (i < text.size() && (text[i] == 'v' || text[i] == 'V')) ++i;
    for (int part = 0; part < 3 && i < text.size(); ++part)
    {
        int value = 0;
        bool has_digit = false;
        while (i < text.size() && text[i] >= '0' && text[i] <= '9')
        {
            has_digit = true;
            value = value * 10 + (text[i] - '0');
            ++i;
        }
        out[part] = has_digit ? value : 0;
        if (i < text.size() && text[i] == '.') { ++i; continue; }
        break;
    }
}

// ---------- 文件 ----------

bool ensure_dir(const std::wstring& path)
{
    if (path.empty()) return false;
    if (CreateDirectoryW(path.c_str(), nullptr)) return true;
    return GetLastError() == ERROR_ALREADY_EXISTS;
}

bool read_text_file(const std::wstring& path, size_t max_bytes, std::string& out)
{
    out.clear();
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart <= 0 ||
        static_cast<unsigned long long>(size.QuadPart) > max_bytes)
    {
        CloseHandle(h);
        return false;
    }

    out.resize(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    const BOOL ok = ReadFile(h, &out[0], static_cast<DWORD>(out.size()), &read, nullptr);
    CloseHandle(h);
    if (!ok) { out.clear(); return false; }
    out.resize(read);
    return true;
}

// 先写 .tmp 再原子替换: 玩家在检查进行到一半时结束游戏, 也不会留下半个 JSON
// 让 mod 读出"有字段缺失"的假状态。
bool write_text_file_atomic(const std::wstring& path, const std::string& text)
{
    const std::wstring tmp = path + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    DWORD written = 0;
    const BOOL ok = text.empty() ? TRUE
                                 : WriteFile(h, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
    CloseHandle(h);
    if (!ok || written != static_cast<DWORD>(text.size()))
    {
        DeleteFileW(tmp.c_str());
        return false;
    }

    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

// ---------- 状态文件 ----------

std::string derive_release_url(const std::string& download_url, const std::string& tag)
{
    // version.json 里只有 zip 的下载地址(.../releases/download/<tag>/<file>)。
    // 把 download 段换成 tag 就是发布页, 不依赖仓库名写死。
    const std::string marker = "/releases/download/";
    const size_t pos = download_url.find(marker);
    if (pos != std::string::npos && !tag.empty())
        return download_url.substr(0, pos) + "/releases/tag/" + tag;
    if (!tag.empty())
        return std::string("https://github.com/") + kRepo + "/releases/tag/" + tag;
    return std::string("https://github.com/") + kRepo + "/releases/latest";
}

bool parse_state_json(const std::string& text, Result& out)
{
    const cesium::jsonc::Json j = cesium::jsonc::parse(text);
    if (!cesium::jsonc::usable(j)) return false;

    out.status = cesium::jsonc::str(j, "status");
    if (out.status.empty()) return false;
    out.latest_version = cesium::jsonc::str(j, "latestVersion");
    out.release_tag = cesium::jsonc::str(j, "releaseTag");
    out.release_date = cesium::jsonc::str(j, "releaseDate");
    out.release_notes = cesium::jsonc::str(j, "releaseNotes");
    out.release_url = cesium::jsonc::str(j, "releaseUrl");
    out.download_url = cesium::jsonc::str(j, "downloadUrl");
    out.download_sha256 = cesium::jsonc::str(j, "downloadSha256");
    out.download_size = cesium::jsonc::integer(j, "downloadSize", 0);
    out.source = cesium::jsonc::str(j, "source");
    out.error = cesium::jsonc::str(j, "error");
    out.checked_at_unix = static_cast<unsigned long long>(
        cesium::jsonc::integer(j, "checkedAtUnix", 0));
    return true;
}

std::string build_state_json(const Result& r)
{
    cesium::jsonc::Json j = cesium::jsonc::Json::object();
    j["schema"] = 1;
    j["status"] = r.status;
    j["currentVersion"] = current_version();
    j["latestVersion"] = r.latest_version;
    j["releaseTag"] = r.release_tag;
    j["releaseDate"] = r.release_date;
    j["releaseNotes"] = r.release_notes;
    j["releaseUrl"] = r.release_url;
    j["downloadUrl"] = r.download_url;
    j["downloadSha256"] = r.download_sha256;
    j["downloadSize"] = r.download_size;
    j["source"] = r.source;
    j["error"] = r.error;
    j["checkedAt"] = iso_utc(r.checked_at_unix);
    j["checkedAtUnix"] = static_cast<long long>(r.checked_at_unix);
    return j.dump(2) + "\n";
}

// 写状态文件 + 刷新内存摘要(两条通道永远同一份事实)。
void publish(const Result& r)
{
    const std::string text = build_state_json(r);
    if (!g_dir.empty())
    {
        const std::wstring path = g_dir + L"\\state.json";
        if (!write_text_file_atomic(path, text))
            log_update("状态文件写入失败: " + narrow_utf8(short_path(path)));
    }

    std::lock_guard<std::mutex> guard(g_mutex);
    g_summary.enabled = g_settings.enabled;
    g_summary.status = r.status;
    g_summary.current_version = current_version();
    g_summary.latest_version = r.latest_version;
    g_summary.release_tag = r.release_tag;
    g_summary.release_date = r.release_date;
    g_summary.release_url = r.release_url;
    g_summary.source = r.source;
    g_summary.error = r.error;
    g_summary.checked_at_unix = r.checked_at_unix;
}

// ---------- HTTPS(WinHTTP 动态解析) ----------

struct WinHttpApi
{
    HMODULE module = nullptr;
    decltype(&WinHttpOpen) open = nullptr;
    decltype(&WinHttpCloseHandle) close = nullptr;
    decltype(&WinHttpConnect) connect = nullptr;
    decltype(&WinHttpOpenRequest) open_request = nullptr;
    decltype(&WinHttpSetTimeouts) set_timeouts = nullptr;
    decltype(&WinHttpSetOption) set_option = nullptr;
    decltype(&WinHttpSendRequest) send = nullptr;
    decltype(&WinHttpReceiveResponse) receive = nullptr;
    decltype(&WinHttpQueryHeaders) query_headers = nullptr;
    decltype(&WinHttpReadData) read = nullptr;
};

const WinHttpApi& winhttp()
{
    static const WinHttpApi api = []
    {
        WinHttpApi a;
        a.module = LoadLibraryW(L"winhttp.dll");
        if (a.module == nullptr) return a;
        auto bind = [&a](const char* name) -> FARPROC { return GetProcAddress(a.module, name); };
        a.open = reinterpret_cast<decltype(a.open)>(bind("WinHttpOpen"));
        a.close = reinterpret_cast<decltype(a.close)>(bind("WinHttpCloseHandle"));
        a.connect = reinterpret_cast<decltype(a.connect)>(bind("WinHttpConnect"));
        a.open_request = reinterpret_cast<decltype(a.open_request)>(bind("WinHttpOpenRequest"));
        a.set_timeouts = reinterpret_cast<decltype(a.set_timeouts)>(bind("WinHttpSetTimeouts"));
        a.set_option = reinterpret_cast<decltype(a.set_option)>(bind("WinHttpSetOption"));
        a.send = reinterpret_cast<decltype(a.send)>(bind("WinHttpSendRequest"));
        a.receive = reinterpret_cast<decltype(a.receive)>(bind("WinHttpReceiveResponse"));
        a.query_headers = reinterpret_cast<decltype(a.query_headers)>(bind("WinHttpQueryHeaders"));
        a.read = reinterpret_cast<decltype(a.read)>(bind("WinHttpReadData"));
        if (a.open == nullptr || a.close == nullptr || a.connect == nullptr ||
            a.open_request == nullptr || a.set_timeouts == nullptr || a.set_option == nullptr ||
            a.send == nullptr || a.receive == nullptr || a.query_headers == nullptr || a.read == nullptr)
        {
            a.module = nullptr;   // 半套 API 不可用 = 整体不可用
        }
        return a;
    }();
    return api;
}

struct ParsedUrl
{
    std::wstring host;
    std::wstring path;
    INTERNET_PORT port = INTERNET_DEFAULT_HTTPS_PORT;
};

bool parse_https_url(const std::string& url, ParsedUrl& out)
{
    if (!starts_with_ci(url, "https://")) return false;
    std::string rest = url.substr(8);
    if (rest.empty()) return false;

    const size_t slash = rest.find('/');
    std::string authority = slash == std::string::npos ? rest : rest.substr(0, slash);
    std::string path = slash == std::string::npos ? "/" : rest.substr(slash);
    if (authority.empty()) return false;
    if (authority.find('@') != std::string::npos) return false;   // 不接受 userinfo

    std::string host = authority;
    INTERNET_PORT port = INTERNET_DEFAULT_HTTPS_PORT;
    const size_t colon = authority.rfind(':');
    if (colon != std::string::npos)
    {
        host = authority.substr(0, colon);
        const std::string port_text = authority.substr(colon + 1);
        if (port_text.empty() || port_text.size() > 5) return false;
        int value = 0;
        for (char c : port_text)
        {
            if (c < '0' || c > '9') return false;
            value = value * 10 + (c - '0');
        }
        if (value <= 0 || value > 65535) return false;
        port = static_cast<INTERNET_PORT>(value);
    }
    if (host.empty()) return false;

    out.host = widen_utf8(host);
    out.path = widen_utf8(path);
    out.port = port;
    return !out.host.empty() && !out.path.empty();
}

// 一次 HTTPS GET。成功(HTTP 200 且读完)返回 true; 否则 error 里带原因。
bool http_get(const std::string& url, unsigned timeout_sec, std::string& body, std::string& error)
{
    body.clear();
    error.clear();

    const WinHttpApi& api = winhttp();
    if (api.module == nullptr)
    {
        error = "winhttp.dll 不可用";
        return false;
    }

    ParsedUrl parsed;
    if (!parse_https_url(url, parsed))
    {
        error = "只支持 https:// 地址";
        return false;
    }

    const int timeout_ms = static_cast<int>(timeout_sec == 0 ? kDefaultTimeoutSec : timeout_sec) * 1000;
    const std::wstring agent = L"CesiumLoader-UpdateCheck/" + widen_utf8(current_version());

    HINTERNET session = api.open(agent.c_str(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                 WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (session == nullptr)
    {
        // WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY 是 Win8.1+ 的取值; 更老的系统退回默认代理。
        session = api.open(agent.c_str(), WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                           WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    }
    if (session == nullptr)
    {
        error = "WinHttpOpen 失败(" + std::to_string(GetLastError()) + ")";
        return false;
    }

    api.set_timeouts(session, kConnectTimeoutMs, kConnectTimeoutMs, timeout_ms, timeout_ms);

    // 至少允许 TLS 1.2: 老的默认值在部分系统上只有 TLS 1.0, 会被 GitHub 直接拒绝。
    DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
#ifdef WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3
    protocols |= WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
#endif
    api.set_option(session, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols));

    HINTERNET connection = api.connect(session, parsed.host.c_str(), parsed.port, 0);
    if (connection == nullptr)
    {
        error = "连不上 " + narrow_utf8(parsed.host) + "(" + std::to_string(GetLastError()) + ")";
        api.close(session);
        return false;
    }

    HINTERNET request = api.open_request(connection, L"GET", parsed.path.c_str(),
                                         nullptr, WINHTTP_NO_REFERER,
                                         WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (request == nullptr)
    {
        error = "WinHttpOpenRequest 失败(" + std::to_string(GetLastError()) + ")";
        api.close(connection);
        api.close(session);
        return false;
    }

    // releases/latest/download/... 是 302 跳到 objects.githubusercontent.com;
    // WinHTTP 默认不跟随重定向, 必须显式打开。
    DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_ALWAYS;
    api.set_option(request, WINHTTP_OPTION_REDIRECT_POLICY, &redirect, sizeof(redirect));

    bool ok = false;
    if (!api.send(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0))
    {
        error = "请求发送失败(" + std::to_string(GetLastError()) + ")";
    }
    else if (!api.receive(request, nullptr))
    {
        error = "没有响应(" + std::to_string(GetLastError()) + ")";
    }
    else
    {
        DWORD status = 0;
        DWORD status_size = sizeof(status);
        if (!api.query_headers(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                               WINHTTP_HEADER_NAME_BY_INDEX, &status, &status_size, WINHTTP_NO_HEADER_INDEX))
        {
            error = "读状态码失败(" + std::to_string(GetLastError()) + ")";
        }
        else if (status != 200)
        {
            error = "HTTP " + std::to_string(status);
        }
        else
        {
            char buffer[8192];
            bool read_ok = true;
            for (;;)
            {
                DWORD got = 0;
                if (!api.read(request, buffer, sizeof(buffer), &got))
                {
                    read_ok = false;
                    error = "读取响应失败(" + std::to_string(GetLastError()) + ")";
                    break;
                }
                if (got == 0) break;
                if (body.size() + got > kMaxManifestBytes)
                {
                    read_ok = false;
                    error = "响应过大(> " + std::to_string(kMaxManifestBytes / 1024) + " KB)";
                    break;
                }
                body.append(buffer, got);
            }
            if (read_ok && body.empty())
            {
                read_ok = false;
                error = "响应为空";
            }
            ok = read_ok;
        }
    }

    api.close(request);
    api.close(connection);
    api.close(session);
    return ok;
}

// 直连 + 镜像候选。返回是否成功; 成功时 source 记录走的是哪条。
bool fetch_manifest(std::string& body, std::string& source, std::string& error)
{
    const std::string direct = g_settings.url.empty() ? std::string(kDefaultManifestUrl) : g_settings.url;

    std::vector<std::pair<std::string, std::string>> candidates;   // {url, source}
    candidates.push_back({ direct, "direct" });

    const std::string mirror = lower_ascii(trim_copy(g_settings.mirror));
    std::string prefix;
    if (mirror == "auto" || mirror.empty())
        prefix = kDefaultMirrorPrefix;
    else if (mirror != "off" && mirror != "none" && mirror != "direct")
        prefix = trim_copy(g_settings.mirror);

    if (!prefix.empty())
    {
        if (!prefix.empty() && prefix.back() != '/') prefix.push_back('/');
        if (starts_with_ci(prefix, "https://"))
            candidates.push_back({ prefix + direct, "mirror" });
    }

    std::string first_error;
    for (size_t i = 0; i < candidates.size(); ++i)
    {
        std::string why;
        if (http_get(candidates[i].first, g_settings.timeout_sec, body, why))
        {
            source = candidates[i].second;
            return true;
        }
        if (first_error.empty()) first_error = why;
        if (candidates.size() > 1)
            log_update(std::string("第 ") + std::to_string(i + 1) + " 条通道失败: " + why);
    }

    error = first_error.empty() ? std::string("未知错误") : first_error;
    return false;
}

// ---------- 解析清单 ----------

struct Manifest
{
    std::string version;
    std::string tag;
    std::string date;
    std::string notes;
    std::string download_url;
    std::string sha256;
    long long size = 0;
};

bool parse_manifest(const std::string& text, Manifest& out, std::string& error)
{
    const cesium::jsonc::Json j = cesium::jsonc::parse(text);
    if (!cesium::jsonc::usable(j))
    {
        error = "清单不是合法 JSON";
        return false;
    }

    out.version = trim_copy(cesium::jsonc::str(j, "version"));
    if (!out.version.empty() && (out.version[0] == 'v' || out.version[0] == 'V'))
        out.version = trim_copy(out.version.substr(1));
    if (out.version.empty())
    {
        error = "清单里没有 version";
        return false;
    }

    out.tag = trim_copy(cesium::jsonc::str(j, "releaseTag"));
    out.date = trim_copy(cesium::jsonc::str(j, "releaseDate"));
    out.notes = cesium::jsonc::str(j, "releaseNotes");

    const cesium::jsonc::Json packages = cesium::jsonc::object(j, "packages");
    const cesium::jsonc::Json loader = cesium::jsonc::object(packages, "loaderZip");
    out.download_url = trim_copy(cesium::jsonc::str(loader, "url"));
    out.sha256 = trim_copy(cesium::jsonc::str(loader, "sha256"));
    out.size = cesium::jsonc::integer(loader, "size", 0);
    return true;
}

// ---------- 检查流程 ----------

bool cache_fresh(const Result& cached)
{
    const unsigned hours = g_settings.interval_hours == 0 ? kDefaultIntervalHours : g_settings.interval_hours;
    if (cached.checked_at_unix == 0) return false;
    const unsigned long long now = now_unix();
    if (cached.checked_at_unix > now) return false;
    return (now - cached.checked_at_unix) < static_cast<unsigned long long>(hours) * 3600ull;
}

std::string hours_text(const Result& cached)
{
    if (cached.checked_at_unix == 0) return "0";
    const unsigned long long now = now_unix();
    if (now <= cached.checked_at_unix) return "0";
    char buf[32] = {};
    snprintf(buf, sizeof(buf), "%.1f",
             static_cast<double>(now - cached.checked_at_unix) / 3600.0);
    return std::string(buf);
}

void run_check(bool force)
{
    if (!g_settings.enabled) return;
    if (g_checking.exchange(true)) return;

    struct Reset
    {
        ~Reset() { g_checking.store(false); }
    } reset;

    // ① 冷却: 上次结果还在有效期内就直接复用, 不重复联网。
    Result cached;
    const std::wstring state_path = g_dir.empty() ? std::wstring() : (g_dir + L"\\state.json");
    std::string state_text;
    const bool has_cache = !state_path.empty() &&
                           read_text_file(state_path, kMaxStateBytes, state_text) &&
                           parse_state_json(state_text, cached);

    if (!force && has_cache && cache_fresh(cached) &&
        (cached.status == "up-to-date" || cached.status == "update-available"))
    {
        {
            std::lock_guard<std::mutex> guard(g_mutex);
            g_summary.enabled = true;
            g_summary.checking = false;
            g_summary.status = cached.status;
            g_summary.current_version = current_version();
            g_summary.latest_version = cached.latest_version;
            g_summary.release_tag = cached.release_tag;
            g_summary.release_date = cached.release_date;
            g_summary.release_url = cached.release_url;
            g_summary.source = cached.source;
            g_summary.error.clear();
            g_summary.checked_at_unix = cached.checked_at_unix;
        }

        const std::string hours = hours_text(cached);
        if (cached.status == "update-available")
            log_update_available("距上次检查 " + hours + "h, 跳过联网; 缓存结果: 有新版本 " + cached.latest_version +
                                 " (当前 " + current_version() + ")");
        else
            log_update("距上次检查 " + hours + "h, 跳过联网; 缓存结果: 已是最新 (" + current_version() + ")");
        return;
    }

    // ② 联网。
    {
        std::lock_guard<std::mutex> guard(g_mutex);
        g_summary.enabled = true;
        g_summary.checking = true;
        g_summary.current_version = current_version();
        g_summary.status = "checking";
    }

    Result result;
    result.checked_at_unix = now_unix();

    std::string body;
    std::string source;
    std::string error;
    if (!fetch_manifest(body, source, error))
    {
        result.status = "error";
        result.error = error;
        result.source = source;
        publish(result);
        log_update("检查失败: " + error + "(不影响游戏)");
        return;
    }

    Manifest manifest;
    if (!parse_manifest(body, manifest, error))
    {
        result.status = "error";
        result.error = error;
        result.source = source;
        publish(result);
        log_update("检查失败: " + error + "(不影响游戏)");
        return;
    }

    // 成功拿到的原文留一份: 排查"为什么解析失败/为什么版本号不对"时不用再联网。
    if (!g_dir.empty())
        write_text_file_atomic(g_dir + L"\\manifest.json", body);

    result.latest_version = manifest.version;
    result.release_tag = manifest.tag;
    result.release_date = manifest.date;
    result.release_notes = manifest.notes;
    result.download_url = manifest.download_url;
    result.download_sha256 = manifest.sha256;
    result.download_size = manifest.size;
    result.source = source;
    result.release_url = derive_release_url(manifest.download_url, manifest.tag);

    const int cmp = cesium::update::compare_versions(manifest.version, current_version());
    result.status = cmp > 0 ? "update-available" : "up-to-date";
    publish(result);

    if (result.status == "update-available")
    {
        log_update_available("发现新版本 " + manifest.version + " (当前 " + current_version() + ") · " +
                             (result.release_tag.empty() ? std::string("-") : result.release_tag) +
                             (source == "mirror" ? " · 镜像" : ""));
        log_update("发布页 " + result.release_url);
    }
    else
    {
        log_update("已是最新 (" + current_version() + ")" + (cmp < 0 ? ", 当前版本比线上更新" : ""));
    }
}

// ---------- UDP IPC ----------

constexpr size_t kMaxDatagramBytes = 4096;

bool allowed_open_host(const std::string& url)
{
    ParsedUrl parsed;
    if (!parse_https_url(url, parsed)) return false;
    const std::string host = lower_ascii(narrow_utf8(parsed.host));
    static const char* const kHosts[] = {
        "github.com", "www.github.com",
        "objects.githubusercontent.com", "raw.githubusercontent.com",
        "gh-proxy.com", "www.gh-proxy.com", "ghfast.top", "www.ghfast.top",
    };
    for (const char* allowed : kHosts)
        if (host == allowed) return true;
    return false;
}

void reply(SOCKET socket, const sockaddr_in& peer, const std::string& text)
{
    sendto(socket, text.c_str(), static_cast<int>(text.size()), 0,
           reinterpret_cast<const sockaddr*>(&peer), sizeof(peer));
}

void ipc_loop(SOCKET socket)
{
    std::vector<char> buffer(kMaxDatagramBytes + 1);
    for (;;)
    {
        sockaddr_in peer{};
        int peer_size = sizeof(peer);
        const int got = recvfrom(socket, buffer.data(), static_cast<int>(buffer.size()), 0,
                                 reinterpret_cast<sockaddr*>(&peer), &peer_size);
        if (got <= 0) continue;

        const std::string message = trim_copy(std::string(buffer.data(), static_cast<size_t>(got)));
        if (message.empty()) continue;

        const size_t space = message.find(' ');
        const std::string command = lower_ascii(space == std::string::npos ? message : message.substr(0, space));
        const std::string argument = space == std::string::npos ? std::string()
                                                               : trim_copy(message.substr(space + 1));

        if (command == "check" || command == "recheck")
        {
            cesium::update::request_check();
            reply(socket, peer, "ok");
        }
        else if (command == "open")
        {
            if (argument.empty())
            {
                reply(socket, peer, "error missing url");
            }
            else if (cesium::update::open_url(argument))
            {
                reply(socket, peer, "ok");
            }
            else
            {
                reply(socket, peer, "error rejected url");
            }
        }
        else
        {
            reply(socket, peer, "error unknown command");
        }
    }
}

void start_ipc()
{
    static std::atomic<bool> started{false};
    if (started.exchange(true)) return;

    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0)
    {
        log_update("UDP 通道不可用: WSAStartup 失败");
        return;
    }

    SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket == INVALID_SOCKET)
    {
        log_update("UDP 通道不可用: socket 创建失败");
        return;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;   // 让系统分配临时端口, 多开互不干扰
    if (bind(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0)
    {
        closesocket(socket);
        log_update("UDP 通道不可用: bind 失败");
        return;
    }

    sockaddr_in bound{};
    int bound_size = sizeof(bound);
    if (getsockname(socket, reinterpret_cast<sockaddr*>(&bound), &bound_size) != 0)
    {
        closesocket(socket);
        log_update("UDP 通道不可用: getsockname 失败");
        return;
    }

    const int port = ntohs(bound.sin_port);
    SetEnvironmentVariableW(L"CESIUM_UPDATE_PORT", std::to_wstring(port).c_str());

    std::thread(ipc_loop, socket).detach();
}

} // namespace

// ============================================================================
// 公开接口
// ============================================================================

namespace cesium::update
{

std::wstring update_dir(const std::wstring& loader_root)
{
    if (loader_root.empty()) return L"update";
    return loader_root + L"\\update";
}

int compare_versions(const std::string& a, const std::string& b)
{
    int pa[3] = { 0, 0, 0 }, pb[3] = { 0, 0, 0 };
    parse_version(a, pa);
    parse_version(b, pb);
    for (int i = 0; i < 3; ++i)
    {
        if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
    }
    return 0;
}

void start(const std::wstring& loader_root, const LoaderConfig& cfg)
{
    static std::once_flag once;
    std::call_once(once, [&]
    {
        g_settings.enabled = cfg.updateCheckEnabled;
        g_settings.url = trim_copy(cfg.updateCheckUrl);
        if (g_settings.url.empty()) g_settings.url = kDefaultManifestUrl;
        g_settings.mirror = cfg.updateCheckMirror;
        g_settings.interval_hours = cfg.updateCheckIntervalHours;
        g_settings.timeout_sec = cfg.updateCheckTimeoutSec;

        g_dir = update_dir(loader_root);
        ensure_dir(g_dir);

        // UDP 通道与"是否联网"无关: 关掉自动检查后, mod 仍可请求强制检查/打开浏览器。
        start_ipc();

        if (!g_settings.enabled)
        {
            Result disabled;
            disabled.status = "disabled";
            disabled.checked_at_unix = now_unix();
            publish(disabled);
            log_update("updateCheckEnabled=false, 跳过联网检查");
            return;
        }

        log_update("检查更新 (当前 " + current_version() + ", 间隔 " +
                   std::to_string(g_settings.interval_hours) + "h)");
        std::thread([] { run_check(false); }).detach();
    });
}

void request_check()
{
    if (!g_settings.enabled) return;
    std::thread([] { run_check(true); }).detach();
}

Summary summary()
{
    std::lock_guard<std::mutex> guard(g_mutex);
    Summary copy = g_summary;
    copy.checking = g_checking.load();
    if (copy.status.empty()) copy.status = copy.enabled ? "idle" : "disabled";
    return copy;
}

bool open_url(const std::string& url)
{
    if (!allowed_open_host(url)) return false;
    const std::wstring wide = widen_utf8(url);
    if (wide.empty()) return false;
    const HINSTANCE result = ShellExecuteW(nullptr, L"open", wide.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    return reinterpret_cast<INT_PTR>(result) > 32;
}

} // namespace cesium::update

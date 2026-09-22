// speedctl.cpp - 变速控制文件通道实现(见 speedctl.h 的协议说明)
//
// 线程模型: 一个常驻线程(Sleep(100) 轮询 request.txt), 随进程退出结束 ——
// 与 forward_activity_log 同款做法(加载器里已有先例, 不需要清理逻辑)。
// 安全性: 只用 kernel32 文件 API + strtod, 所有失败路径都只记日志;
// 线程里不分配窗口/不碰 loader lock(由 boot 线程在 loader lock 释放后启动)。
//
// 双目录: 状态文件同时写 <加载器>\speed 和 %LocalAppData%\AstralParty_ModLoader\speed,
// 并同时监听两处的 request.txt。原因是 mod 侧(热更程序集)定位目录只能靠环境变量和
// 路径推导, 任何一环出问题就"按了没反应"; 两个位置都摆一份, 只要 mod 命中其中一个
// 通道就是通的。两处的请求都能生效, 谁变了就用谁(各自记 last_seen)。

#include "speedctl.h"

#include "config.h"
#include "loader.h"
#include "speedhack.h"

#include <cctype>
#include <cstdlib>
#include <cstdio>
#include <string>

#include <fmt/format.h>

namespace
{

const wchar_t* kRequestName = L"request.txt";
const wchar_t* kStateName = L"state.txt";

// 轮询间隔(ms)。100ms 在"按键手感"和"CPU 开销"之间取平衡: 人眼基本感知不到延迟,
// 一次 stat+read 的代价可以忽略(而且只在文件变化时才真正读+应用)。
const DWORD kPollMs = 100;

// 请求文件上限: 正常内容只有几个字节, 超过这个长度的一律当垃圾(避免读到奇怪文件)。
const long long kMaxBytes = 4096;

// 控制目录(至少 1 个, 最多 2 个): 主目录 + LocalAppData 镜像。
const int kMaxDirs = 2;
std::wstring g_dirs[kMaxDirs];
int g_dir_count = 0;

double g_base = 1.0;
volatile LONG g_started = 0;

// %LocalAppData%\AstralParty_ModLoader\speed —— SDK 的最后一档候选目录,
// 与环境变量推导无关, 所以即使 mod 读不到环境变量也能命中。
std::wstring local_appdata_mirror()
{
    wchar_t buf[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return std::wstring();
    return std::wstring(buf) + L"\\AstralParty_ModLoader\\speed";
}

std::wstring path_of(const std::wstring& dir, const wchar_t* name)
{
    return dir + L"\\" + name;
}

// 逐级创建(目标是 <...>\AstralParty_ModLoader\speed, 上层目录可能还不存在 ——
// CreateDirectoryW 不会自动建中间层)。
bool ensure_dir(const std::wstring& dir)
{
    if (dir.empty()) return false;

    DWORD attr = GetFileAttributesW(dir.c_str());
    if (attr != INVALID_FILE_ATTRIBUTES) return (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;

    size_t slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos && slash > 2)   // >2 跳过 "C:\" 这种根
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
    if (!GetFileSizeEx(h, &size) || size.QuadPart > kMaxBytes) { CloseHandle(h); return false; }

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

// 原子写: 先写 .tmp 再 MoveFileEx 覆盖 —— 避免 mod 侧读到"写了一半"的内容。
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

// 倍率文本: 固定 3 位小数 + 点号(与 mod 侧 InvariantCulture 一致, 与系统区域无关)。
std::string fmt_speed(double v)
{
    // fmt 的格式化与系统区域无关(小数点固定为 '.'), 行为与原来的 _snprintf_s 一致。
    return fmt::format("{:.3f}", v);
}

// 严格解析: 只接受 [+-]?数字[.数字] 形式的十进制, 范围 [kSpeedMin, kSpeedMax] = [1, 100]。
// 注意不能直接丢给 strtod: 它还会接受 0x2(十六进制浮点)/1e5(指数) 这类写法,
// 手改或损坏的文件就会变成意料之外的倍率。NaN 由 "!(v >= kSpeedMin && v <= kSpeedMax)" 挡掉。
// 低于 1 倍的请求(0.5、0.999…)在这里就被拦掉, 绝不落到引擎上 —— 这是"禁止减速"的第一道闸。
bool parse_speed(const std::string& text, double& out)
{
    size_t i = 0, n = text.size();
    while (i < n && std::isspace((unsigned char)text[i])) i++;
    size_t start = i;

    if (i < n && (text[i] == '+' || text[i] == '-')) i++;

    int digits = 0, dots = 0;
    while (i < n && (std::isdigit((unsigned char)text[i]) || text[i] == '.'))
    {
        if (text[i] == '.') dots++;
        else digits++;
        i++;
    }
    if (digits == 0 || dots > 1) return false;

    while (i < n && std::isspace((unsigned char)text[i])) i++;
    if (i != n) return false;   // 还有多余的字符(x / e / 第二个小数点等) -> 一律拒绝

    double v = std::strtod(text.c_str() + start, nullptr);
    if (!(v >= kSpeedMin && v <= kSpeedMax)) return false;
    out = v;
    return true;
}

void write_state()
{
    bool active = speedhack_active();
    int hooks = speedhack_hook_count();
    double speed = speedhack_get_speed();

    std::string s;
    s += "version="; s += LoaderConfig::loaderVersion; s += "\r\n";
    s += "speed=";   s += fmt_speed(speed);            s += "\r\n";
    s += "base=";    s += fmt_speed(g_base);           s += "\r\n";
    s += "active=";  s += active ? "1" : "0";          s += "\r\n";
    s += "hooks=";   s += std::to_string(hooks);       s += "\r\n";

    for (int i = 0; i < g_dir_count; i++)
    {
        if (!write_text(path_of(g_dirs[i], kStateName), s))
            log_line(L"[speedctl] 状态文件写入失败: " + path_of(g_dirs[i], kStateName));
    }
}

// 初始状态(在 boot 线程里**同步**写, 不能只靠监听线程抢跑):
//   - request.txt 重置成"当前引擎倍率"(加载器已应用基础倍率), 这样上一次会话残留的
//     请求不会被误当成新请求;
//   - 立刻写出 state.txt, 保证 mod 初始化时一定能读到"引擎可用 + 当前倍率"。
void write_initial_files()
{
    std::string init = fmt_speed(speedhack_get_speed()) + "\r\n";
    for (int i = 0; i < g_dir_count; i++)
    {
        if (!write_text(path_of(g_dirs[i], kRequestName), init))
            log_line(L"[speedctl] 初始 request.txt 写入失败, 变速热键仍可通过新写入生效: " +
                     path_of(g_dirs[i], kRequestName));
    }
    write_state();
}

DWORD WINAPI watch_thread(LPVOID)
{
    for (int i = 0; i < g_dir_count; i++)
        log_line(L"[speedctl] 变速控制文件通道就绪 (目录: " + g_dirs[i] + L")");

    std::string last_seen[kMaxDirs];
    for (int i = 0; i < g_dir_count; i++)
    {
        if (!read_text(path_of(g_dirs[i], kRequestName), last_seen[i]))
            log_line(L"[speedctl] 读取 request.txt 失败, 等待 mod 写入: " +
                     path_of(g_dirs[i], kRequestName));
    }

    for (;;)
    {
        Sleep(kPollMs);

        for (int i = 0; i < g_dir_count; i++)
        {
            std::string text;
            if (!read_text(path_of(g_dirs[i], kRequestName), text)) continue;
            if (text == last_seen[i]) continue;
            last_seen[i] = text;

            double want = 0.0;
            if (!parse_speed(text, want))
            {
                // 非法请求: 保持当前倍率(不 crash、不改变游戏状态), 只记日志。
                std::string brief = text.substr(0, 32);
                for (size_t k = 0; k < brief.size(); k++)
                    if (brief[k] == '\r' || brief[k] == '\n') brief[k] = ' ';
                log_line("[speedctl] 忽略非法倍率请求: '" + brief + "' (只接受 [1,100] 的单个数字; 低于 1 倍被禁止)");
                continue;
            }

            if (speedhack_set_speed(want))
            {
                log_line("[speedctl] mod 请求倍率 -> " + fmt_speed(speedhack_get_speed()) + "x");
            }
            else
            {
                log_line("[speedctl] 应用倍率失败(变速引擎不可用): " + fmt_speed(want));
            }
            write_state();   // 无论成功与否都回写, mod 读到的是真实值
        }
    }
    return 0;
}

} // namespace

std::wstring speedctl_dir()
{
    return loader_root() + L"\\speed";
}

void speedctl_start(const std::wstring& dir, double base_speed)
{
    // 幂等: boot 线程只会启动一次, 但用 InterlockedExchange 保证重复调用安全。
    if (InterlockedExchange(&g_started, 1) != 0) return;

    // 基准倍率同样受硬下限约束: doorstop/Toys 里配了 <1 的值也一律抬到 1.0(不允许减速)。
    if (base_speed >= kSpeedMin && base_speed <= kSpeedMax)
    {
        g_base = base_speed;
    }
    else
    {
        g_base = 1.0;
        if (base_speed > 0.0 && base_speed < kSpeedMin)
            log_line("[speedctl] speedhackBaseSpeed=" + fmt_speed(base_speed) +
                     " 低于下限 1.0, 已按 1.0 处理(不允许减速)");
        else
            log_line("[speedctl] speedhackBaseSpeed 非法, 已按 1.0 处理");
    }

    // 主目录 + LocalAppData 镜像(后者失败不影响前者)。
    g_dir_count = 0;
    if (!dir.empty()) g_dirs[g_dir_count++] = dir;

    std::wstring mirror = local_appdata_mirror();
    if (!mirror.empty())
    {
        if (mirror == dir)
            log_line("[speedctl] 镜像目录与主目录相同, 只使用一份");
        else
            g_dirs[g_dir_count++] = mirror;
    }
    else
    {
        log_line("[speedctl] 取不到 %LOCALAPPDATA%, 只使用主目录");
    }

    // 逐目录创建, 丢掉创建失败的(至少一个可用, 通道才算可用)。
    std::wstring kept[kMaxDirs];
    int keptCount = 0;
    for (int i = 0; i < g_dir_count; i++)
    {
        if (ensure_dir(g_dirs[i])) { kept[keptCount++] = g_dirs[i]; }
        else log_line(L"[speedctl] 控制目录创建失败, 该目录不可用: " + g_dirs[i]);
    }
    for (int i = 0; i < keptCount; i++) g_dirs[i] = kept[i];
    g_dir_count = keptCount;

    if (g_dir_count == 0)
    {
        log_line("[speedctl] 没有可用的控制目录, 变速控制文件通道不可用");
        return;
    }

    // 同步写初始文件: mod 在 boot 线程后续阶段加载, 此时 state.txt 必须已存在,
    // 否则 mod 会误判"引擎不可用"(见 write_initial_files 的说明)。
    write_initial_files();

    HANDLE h = CreateThread(nullptr, 0, watch_thread, nullptr, 0, nullptr);
    if (h)
    {
        CloseHandle(h);
        log_line("[speedctl] 监听目录数: " + std::to_string(g_dir_count));
        return;
    }

    // 线程起不来的话, request.txt 没人听 —— 必须让 mod 判定为"不可用"(而不是按了没反应):
    // 删掉状态文件, SDK 的 IsAvailable 就会因为"读不到状态文件"而返回 false。
    log_line("[speedctl] 监听线程创建失败, 变速控制文件通道不可用");
    for (int i = 0; i < g_dir_count; i++)
        DeleteFileW(path_of(g_dirs[i], kStateName).c_str());
}

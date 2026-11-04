// fsredir.cpp - 文件 API 前缀重定向(说明见 fsredir.h)
//
// 实现要点:
//   1. MinHook 是**进程级单例**(speedhack.cpp 的 speedhack_init() 已经调过
//      MH_Initialize()), 因此 MH_ERROR_ALREADY_INITIALIZED 必须当成成功;
//      本模块**不**调用 MH_Uninitialize() —— 那是 speedhack 的所有权。
//   2. hook 里绝不能调用被 hook 的 API: 所有真实函数指针在安装时缓存到 Real_*。
//   3. 前缀匹配用大小写不敏感 + 正反斜杠等价; 相对路径/UNC/设备路径放过。
//   4. 写打开时补建父目录(新 profile 里的 BnSdk\<uid>\... 这类多级目录,
//      游戏不一定逐级 CreateDirectoryW)。
//   5. 诊断日志有预算: 只打前 N 条改写, 之后按 1000 条一次打汇总 —— hook 在
//      多线程热路径上, 不能每个调用都写盘。

#include "fsredir/fsredir.h"

#include "platform/loader.h"

#include <MinHook.h>

#include <atomic>
#include <cstring>
#include <cwchar>
#include <string>

namespace
{

// ---------- 重定向前缀(安装后只读, 可被任意线程并发读) ----------

constexpr size_t kMaxPrefixChars = 1024;

wchar_t g_src[kMaxPrefixChars] = {};         // source 原样(展示用)
wchar_t g_src_lower[kMaxPrefixChars] = {};   // source 小写 + 统一反斜杠(匹配用)
wchar_t g_dst[kMaxPrefixChars] = {};         // target 原样
size_t g_src_len = 0;
size_t g_dst_len = 0;
std::atomic<bool> g_active{false};

// 诊断计数
std::atomic<long> g_rewrites{0};
std::atomic<long> g_open_failures{0};
std::atomic<int> g_log_budget{60};
// 改写后调用失败也要能查: 少挂一个 API(例如 il2cpp 的 File.Copy 走 CopyFileExW)时,
// 表现是"删除被重定向、复制落在原目录", 光看日志会以为一切正常。失败单独记一小段。
std::atomic<long> g_failures{0};
std::atomic<int> g_fail_budget{24};

// ---------- 真实函数 ----------

typedef HANDLE(WINAPI* Fn_CreateFileW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef BOOL(WINAPI* Fn_CreateDirectoryW)(LPCWSTR, LPSECURITY_ATTRIBUTES);
typedef BOOL(WINAPI* Fn_RemoveDirectoryW)(LPCWSTR);
typedef BOOL(WINAPI* Fn_DeleteFileW)(LPCWSTR);
typedef DWORD(WINAPI* Fn_GetFileAttributesW)(LPCWSTR);
typedef BOOL(WINAPI* Fn_GetFileAttributesExW)(LPCWSTR, GET_FILEEX_INFO_LEVELS, LPVOID);
typedef HANDLE(WINAPI* Fn_FindFirstFileW)(LPCWSTR, LPWIN32_FIND_DATAW);
typedef HANDLE(WINAPI* Fn_FindFirstFileExW)(LPCWSTR, FINDEX_INFO_LEVELS, LPVOID, FINDEX_SEARCH_OPS, LPVOID, DWORD);
typedef BOOL(WINAPI* Fn_MoveFileW)(LPCWSTR, LPCWSTR);
typedef BOOL(WINAPI* Fn_MoveFileExW)(LPCWSTR, LPCWSTR, DWORD);
typedef BOOL(WINAPI* Fn_CopyFileW)(LPCWSTR, LPCWSTR, BOOL);
typedef BOOL(WINAPI* Fn_CopyFileExW)(LPCWSTR, LPCWSTR, LPPROGRESS_ROUTINE, LPVOID, LPBOOL, DWORD);
typedef BOOL(WINAPI* Fn_ReplaceFileW)(LPCWSTR, LPCWSTR, LPCWSTR, DWORD, LPVOID, LPVOID);
typedef BOOL(WINAPI* Fn_SetFileAttributesW)(LPCWSTR, DWORD);

Fn_CreateFileW Real_CreateFileW = nullptr;
Fn_CreateDirectoryW Real_CreateDirectoryW = nullptr;
Fn_RemoveDirectoryW Real_RemoveDirectoryW = nullptr;
Fn_DeleteFileW Real_DeleteFileW = nullptr;
Fn_GetFileAttributesW Real_GetFileAttributesW = nullptr;
Fn_GetFileAttributesExW Real_GetFileAttributesExW = nullptr;
Fn_FindFirstFileW Real_FindFirstFileW = nullptr;
Fn_FindFirstFileExW Real_FindFirstFileExW = nullptr;
Fn_MoveFileW Real_MoveFileW = nullptr;
Fn_MoveFileExW Real_MoveFileExW = nullptr;
Fn_CopyFileW Real_CopyFileW = nullptr;
Fn_CopyFileExW Real_CopyFileExW = nullptr;
Fn_ReplaceFileW Real_ReplaceFileW = nullptr;
Fn_SetFileAttributesW Real_SetFileAttributesW = nullptr;

// ---------- 工具 ----------

std::string to_utf8(const wchar_t* p, size_t len)
{
    if (!p || len == 0) return "";
    const int n = WideCharToMultiByte(CP_UTF8, 0, p, (int)len, nullptr, 0, nullptr, nullptr);
    if (n <= 0) return "";
    std::string s((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, p, (int)len, s.data(), n, nullptr, nullptr);
    return s;
}

inline wchar_t norm_lower(wchar_t c)
{
    return (c >= L'A' && c <= L'Z') ? (wchar_t)(c + (L'a' - L'A')) : c;
}

inline wchar_t norm_sep(wchar_t c)
{
    return c == L'/' ? L'\\' : c;
}

// 前缀改写: 命中 source(含其子路径)则写入 out 并返回 true。
bool rewrite_path(const wchar_t* in, std::wstring& out)
{
    if (!g_active.load(std::memory_order_acquire) || !in || !*in) return false;

    const wchar_t* p = in;
    const wchar_t* keep = nullptr;   // 长路径前缀原样保留

    if (p[0] == L'\\' && p[1] == L'\\' && p[2] == L'?' && p[3] == L'\\')
    {
        // \\?\UNC\... 是网络路径, 不在重定向范围
        if (p[4] == L'U' || p[4] == L'u') return false;
        keep = L"\\\\?\\";
        p += 4;
    }
    else if (p[0] == L'\\' && p[1] == L'?' && p[2] == L'?' && p[3] == L'\\')
    {
        keep = L"\\??\\";
        p += 4;
    }

    // 只处理 "X:\..." 形式的绝对路径
    if (!p[0] || p[1] != L':') return false;

    const size_t len = std::wcslen(p);
    if (len < g_src_len) return false;
    for (size_t i = 0; i < g_src_len; ++i)
    {
        if (norm_sep(norm_lower(p[i])) != g_src_lower[i]) return false;
    }
    if (len > g_src_len && p[g_src_len] != L'\\' && p[g_src_len] != L'/') return false;

    out.clear();
    if (keep) out.append(keep);
    out.append(g_dst);
    out.append(p + g_src_len, len - g_src_len);
    return true;
}

void note(const char* api, const std::wstring& path, const char* suffix = "")
{
    const long total = g_rewrites.fetch_add(1, std::memory_order_relaxed) + 1;

    if (g_log_budget.load(std::memory_order_relaxed) > 0)
    {
        if (g_log_budget.fetch_sub(1, std::memory_order_relaxed) > 0)
        {
            std::string line = std::string("[fsredir] ") + api + " -> " + to_utf8(path.c_str(), path.size());
            line += suffix;
            log_line(line);
            return;
        }
        g_log_budget.fetch_add(1, std::memory_order_relaxed);   // 抢输了, 还回去
        return;
    }

    if (total % 1000 == 0)
    {
        log_line("[fsredir] " + std::to_string(total) + " path rewrites so far (" +
                 std::to_string(g_open_failures.load(std::memory_order_relaxed)) + " failed opens)");
    }
}

// 改写后的调用失败: 这里必须记下来。少挂一个 API 时(例如 il2cpp 的 File.Copy 走
// CopyFileExW), 表现是删除被改写到 profile、复制仍落在原目录 —— 游戏侧只会看到
// "文件已存在"之类的 IOException, 光看成功日志完全看不出重定向漏了哪个函数。
void note_failure(const char* api, const std::wstring& path, DWORD error)
{
    g_failures.fetch_add(1, std::memory_order_relaxed);

    if (g_fail_budget.load(std::memory_order_relaxed) <= 0) return;
    if (g_fail_budget.fetch_sub(1, std::memory_order_relaxed) <= 0)
    {
        g_fail_budget.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    log_line(std::string("[fsredir] ") + api + " failed (error " + std::to_string(error) + ") -> " +
             to_utf8(path.c_str(), path.size()));
}

// 写打开时补建父目录(只补 target 之下的层级)
void ensure_parent_dir(const std::wstring& path)
{
    // 同一线程连续写同一个目录时省掉重复调用
    static thread_local std::wstring last_parent;
    static thread_local bool has_last = false;

    const size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos || slash <= g_dst_len) return;

    const std::wstring parent = path.substr(0, slash);
    if (has_last && parent == last_parent) return;
    last_parent = parent;
    has_last = true;

    for (size_t i = g_dst_len + 1; i <= parent.size(); ++i)
    {
        if (i != parent.size() && parent[i] != L'\\' && parent[i] != L'/') continue;
        const std::wstring sub = parent.substr(0, i);
        if (Real_CreateDirectoryW) Real_CreateDirectoryW(sub.c_str(), nullptr);
    }
}

bool wants_write(DWORD access, DWORD disposition)
{
    constexpr DWORD kWriteAccess = GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA |
                                   FILE_WRITE_ATTRIBUTES | WRITE_DAC | WRITE_OWNER;
    if (access & kWriteAccess) return true;
    return disposition == CREATE_ALWAYS || disposition == CREATE_NEW || disposition == OPEN_ALWAYS ||
           disposition == TRUNCATE_EXISTING;
}

// ---------- hook ----------

HANDLE WINAPI Hook_CreateFileW(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa,
                               DWORD disposition, DWORD flags, HANDLE tmpl)
{
    std::wstring path;
    if (!rewrite_path(name, path))
        return Real_CreateFileW(name, access, share, sa, disposition, flags, tmpl);

    if (wants_write(access, disposition)) ensure_parent_dir(path);

    const HANDLE h = Real_CreateFileW(path.c_str(), access, share, sa, disposition, flags, tmpl);
    if (h == INVALID_HANDLE_VALUE)
    {
        const DWORD err = GetLastError();
        g_open_failures.fetch_add(1, std::memory_order_relaxed);
        note_failure("CreateFileW", path, err);
    }
    else
    {
        note("CreateFileW", path);
    }
    return h;
}

// 目录探测类 API 的"正常失败"(不存在 / 已存在)不记 —— 游戏每帧都在试。
bool benign_probe_error(DWORD error)
{
    return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ||
           error == ERROR_ALREADY_EXISTS || error == ERROR_DIR_NOT_EMPTY ||
           error == ERROR_ACCESS_DENIED;
}

BOOL WINAPI Hook_CreateDirectoryW(LPCWSTR name, LPSECURITY_ATTRIBUTES sa)
{
    std::wstring path;
    if (!rewrite_path(name, path)) return Real_CreateDirectoryW(name, sa);

    const BOOL ok = Real_CreateDirectoryW(path.c_str(), sa);
    if (ok)
    {
        note("CreateDirectoryW", path);
    }
    else
    {
        const DWORD err = GetLastError();
        if (!benign_probe_error(err)) note_failure("CreateDirectoryW", path, err);
    }
    return ok;
}

BOOL WINAPI Hook_RemoveDirectoryW(LPCWSTR name)
{
    std::wstring path;
    if (!rewrite_path(name, path)) return Real_RemoveDirectoryW(name);

    const BOOL ok = Real_RemoveDirectoryW(path.c_str());
    if (ok)
    {
        note("RemoveDirectoryW", path);
    }
    else
    {
        const DWORD err = GetLastError();
        if (!benign_probe_error(err)) note_failure("RemoveDirectoryW", path, err);
    }
    return ok;
}

BOOL WINAPI Hook_DeleteFileW(LPCWSTR name)
{
    std::wstring path;
    if (!rewrite_path(name, path)) return Real_DeleteFileW(name);

    const BOOL ok = Real_DeleteFileW(path.c_str());
    if (ok)
    {
        note("DeleteFileW", path);
    }
    else
    {
        const DWORD err = GetLastError();
        if (!benign_probe_error(err)) note_failure("DeleteFileW", path, err);
    }
    return ok;
}

DWORD WINAPI Hook_GetFileAttributesW(LPCWSTR name)
{
    std::wstring path;
    if (!rewrite_path(name, path)) return Real_GetFileAttributesW(name);
    return Real_GetFileAttributesW(path.c_str());
}

BOOL WINAPI Hook_GetFileAttributesExW(LPCWSTR name, GET_FILEEX_INFO_LEVELS level, LPVOID info)
{
    std::wstring path;
    if (!rewrite_path(name, path)) return Real_GetFileAttributesExW(name, level, info);
    return Real_GetFileAttributesExW(path.c_str(), level, info);
}

BOOL WINAPI Hook_SetFileAttributesW(LPCWSTR name, DWORD attributes)
{
    std::wstring path;
    if (!rewrite_path(name, path)) return Real_SetFileAttributesW(name, attributes);

    const BOOL ok = Real_SetFileAttributesW(path.c_str(), attributes);
    if (!ok)
    {
        const DWORD err = GetLastError();
        if (!benign_probe_error(err)) note_failure("SetFileAttributesW", path, err);
    }
    return ok;
}

HANDLE WINAPI Hook_FindFirstFileW(LPCWSTR spec, LPWIN32_FIND_DATAW data)
{
    std::wstring path;
    if (!rewrite_path(spec, path)) return Real_FindFirstFileW(spec, data);
    return Real_FindFirstFileW(path.c_str(), data);
}

HANDLE WINAPI Hook_FindFirstFileExW(LPCWSTR spec, FINDEX_INFO_LEVELS level, LPVOID data,
                                   FINDEX_SEARCH_OPS search, LPVOID filter, DWORD flags)
{
    std::wstring path;
    if (!rewrite_path(spec, path)) return Real_FindFirstFileExW(spec, level, data, search, filter, flags);
    return Real_FindFirstFileExW(path.c_str(), level, data, search, filter, flags);
}

BOOL WINAPI Hook_MoveFileW(LPCWSTR from, LPCWSTR to)
{
    std::wstring a, b;
    const bool ra = rewrite_path(from, a);
    const bool rb = rewrite_path(to, b);
    if (!ra && !rb) return Real_MoveFileW(from, to);

    const BOOL ok = Real_MoveFileW(ra ? a.c_str() : from, rb ? b.c_str() : to);
    const std::wstring shown = rb ? b : (ra ? a : std::wstring());
    if (ok) note("MoveFileW", shown);
    else note_failure("MoveFileW", shown, GetLastError());
    return ok;
}

BOOL WINAPI Hook_MoveFileExW(LPCWSTR from, LPCWSTR to, DWORD flags)
{
    std::wstring a, b;
    const bool ra = rewrite_path(from, a);
    const bool rb = rewrite_path(to, b);
    if (!ra && !rb) return Real_MoveFileExW(from, to, flags);

    const BOOL ok = Real_MoveFileExW(ra ? a.c_str() : from, rb ? b.c_str() : to, flags);
    const std::wstring shown = rb ? b : (ra ? a : std::wstring());
    if (ok) note("MoveFileExW", shown);
    else note_failure("MoveFileExW", shown, GetLastError());
    return ok;
}

BOOL WINAPI Hook_CopyFileW(LPCWSTR from, LPCWSTR to, BOOL fail_if_exists)
{
    std::wstring a, b;
    const bool ra = rewrite_path(from, a);
    const bool rb = rewrite_path(to, b);
    if (!ra && !rb) return Real_CopyFileW(from, to, fail_if_exists);

    const BOOL ok = Real_CopyFileW(ra ? a.c_str() : from, rb ? b.c_str() : to, fail_if_exists);
    const std::wstring shown = rb ? b : (ra ? a : std::wstring());
    if (ok) note("CopyFileW", shown);
    else note_failure("CopyFileW", shown, GetLastError());
    return ok;
}

// il2cpp 的 System.IO.File.Copy 走这里(GameAssembly.dll 导入 CopyFileExW)。
BOOL WINAPI Hook_CopyFileExW(LPCWSTR from, LPCWSTR to, LPPROGRESS_ROUTINE progress, LPVOID data,
                            LPBOOL cancel, DWORD flags)
{
    std::wstring a, b;
    const bool ra = rewrite_path(from, a);
    const bool rb = rewrite_path(to, b);
    if (!ra && !rb) return Real_CopyFileExW(from, to, progress, data, cancel, flags);

    if (rb) ensure_parent_dir(b);

    const BOOL ok = Real_CopyFileExW(ra ? a.c_str() : from, rb ? b.c_str() : to, progress, data, cancel, flags);
    const std::wstring shown = rb ? b : (ra ? a : std::wstring());
    if (ok) note("CopyFileExW", shown);
    else note_failure("CopyFileExW", shown, GetLastError());
    return ok;
}

// 原子替换(三个路径都要改写: 被替换的文件 / 替换者 / 可选备份)。
BOOL WINAPI Hook_ReplaceFileW(LPCWSTR replaced, LPCWSTR replacement, LPCWSTR backup, DWORD flags,
                              LPVOID exclude, LPVOID reserved)
{
    std::wstring a, b, c;
    const bool ra = replaced && rewrite_path(replaced, a);
    const bool rb = replacement && rewrite_path(replacement, b);
    const bool rc = backup && rewrite_path(backup, c);
    if (!ra && !rb && !rc) return Real_ReplaceFileW(replaced, replacement, backup, flags, exclude, reserved);

    const BOOL ok = Real_ReplaceFileW(ra ? a.c_str() : replaced, rb ? b.c_str() : replacement,
                                      rc ? c.c_str() : backup, flags, exclude, reserved);
    const std::wstring shown = ra ? a : (rb ? b : c);
    if (ok) note("ReplaceFileW", shown);
    else note_failure("ReplaceFileW", shown, GetLastError());
    return ok;
}

} // namespace

namespace cesium::fsredir
{

bool active()
{
    return g_active.load(std::memory_order_acquire);
}

bool install(const std::wstring& source_dir, const std::wstring& target_dir)
{
    if (g_active.load()) return true;

    // 去掉尾随分隔符(profile 模块给的路径本就没有, 这里防御)
    std::wstring src = source_dir;
    std::wstring dst = target_dir;
    while (src.size() > 3 && (src.back() == L'\\' || src.back() == L'/')) src.pop_back();
    while (dst.size() > 3 && (dst.back() == L'\\' || dst.back() == L'/')) dst.pop_back();

    if (src.empty() || dst.empty())
    {
        log_line("[fsredir] empty prefix, redirection not installed");
        return false;
    }
    if (src.size() >= kMaxPrefixChars || dst.size() >= kMaxPrefixChars)
    {
        log_line("[fsredir] path too long, redirection not installed");
        return false;
    }

    wcscpy_s(g_src, kMaxPrefixChars, src.c_str());
    wcscpy_s(g_dst, kMaxPrefixChars, dst.c_str());
    g_src_len = src.size();
    g_dst_len = dst.size();
    for (size_t i = 0; i < g_src_len; ++i) g_src_lower[i] = norm_sep(norm_lower(src[i]));
    g_src_lower[g_src_len] = 0;

    MH_STATUS st = MH_Initialize();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED)
    {
        log_line("[fsredir] MinHook init failed: " + std::to_string((int)st));
        return false;
    }

    struct Spec
    {
        const char* name;
        LPVOID detour;
        LPVOID* original;
    };
    Spec specs[] = {
        { "CreateFileW", (LPVOID)&Hook_CreateFileW, (LPVOID*)&Real_CreateFileW },
        { "CreateDirectoryW", (LPVOID)&Hook_CreateDirectoryW, (LPVOID*)&Real_CreateDirectoryW },
        { "RemoveDirectoryW", (LPVOID)&Hook_RemoveDirectoryW, (LPVOID*)&Real_RemoveDirectoryW },
        { "DeleteFileW", (LPVOID)&Hook_DeleteFileW, (LPVOID*)&Real_DeleteFileW },
        { "GetFileAttributesW", (LPVOID)&Hook_GetFileAttributesW, (LPVOID*)&Real_GetFileAttributesW },
        { "GetFileAttributesExW", (LPVOID)&Hook_GetFileAttributesExW, (LPVOID*)&Real_GetFileAttributesExW },
        { "FindFirstFileW", (LPVOID)&Hook_FindFirstFileW, (LPVOID*)&Real_FindFirstFileW },
        { "FindFirstFileExW", (LPVOID)&Hook_FindFirstFileExW, (LPVOID*)&Real_FindFirstFileExW },
        { "MoveFileW", (LPVOID)&Hook_MoveFileW, (LPVOID*)&Real_MoveFileW },
        { "MoveFileExW", (LPVOID)&Hook_MoveFileExW, (LPVOID*)&Real_MoveFileExW },
        { "CopyFileW", (LPVOID)&Hook_CopyFileW, (LPVOID*)&Real_CopyFileW },
        // il2cpp(游戏托管的 System.IO)复制文件用的是 CopyFileExW —— 只挂 CopyFileW 会漏掉它,
        // 表现为"删除被改写到 profile、复制仍落在原目录", ES3 存盘时报"文件已存在"并把登录流程打断。
        { "CopyFileExW", (LPVOID)&Hook_CopyFileExW, (LPVOID*)&Real_CopyFileExW },
        { "ReplaceFileW", (LPVOID)&Hook_ReplaceFileW, (LPVOID*)&Real_ReplaceFileW },
        { "SetFileAttributesW", (LPVOID)&Hook_SetFileAttributesW, (LPVOID*)&Real_SetFileAttributesW },
    };

    const int total = (int)(sizeof(specs) / sizeof(specs[0]));
    int created = 0;
    std::string failed;

    for (const Spec& s : specs)
    {
        const MH_STATUS r = MH_CreateHookApi(L"kernel32.dll", s.name, s.detour, s.original);
        if (r == MH_OK) { ++created; continue; }
        if (!failed.empty()) failed += ", ";
        failed += s.name;
    }

    if (created == 0)
    {
        log_line("[fsredir] no hook could be created, redirection not installed");
        return false;
    }

    // 使能前先置 active: 第一条 enable 之后其它线程就可能进 hook, 前缀必须已就绪
    g_active.store(true, std::memory_order_release);

    // 一次性使能(MH_ALL_HOOKS): MinHook 每调用一次 MH_EnableHook 都要暂停/恢复
    // 进程内所有线程, 11 个 hook 逐个使能实测要 ~3s —— 这段时间游戏已经在写文件了。
    // 此刻进程里只有本模块刚创建的这 11 个 hook(speedhack/steamhack 都在其后安装),
    // 所以"全使能"等价于"只使能自己的"。
    const MH_STATUS en = MH_EnableHook(MH_ALL_HOOKS);
    if (en != MH_OK)
    {
        g_active.store(false, std::memory_order_release);
        log_line("[fsredir] MH_EnableHook failed: " + std::to_string((int)en) + "; redirection not installed");
        return false;
    }

    log_line("[fsredir] installed " + std::to_string(created) + "/" + std::to_string(total) + " file hooks");
    if (!failed.empty()) log_line("[fsredir] could not hook: " + failed);
    log_line("[fsredir]   " + to_utf8(src.c_str(), src.size()) + "  ->  " + to_utf8(dst.c_str(), dst.size()));
    return true;
}

} // namespace cesium::fsredir

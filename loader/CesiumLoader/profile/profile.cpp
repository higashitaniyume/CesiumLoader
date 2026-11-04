// profile.cpp - profile 目录的解析 / 创建 / 缓存播种(说明见 profile.h)

#include "profile/profile.h"

#include "platform/loader.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <memory>
#include <vector>

namespace
{

// ---------- 宽/窄转换 ----------

std::string to_utf8(const std::wstring& w)
{
    if (w.empty()) return "";
    const int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (len <= 0) return "";
    std::string s((size_t)len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), len, nullptr, nullptr);
    return s;
}

std::wstring from_utf8(const std::string& s)
{
    if (s.empty()) return L"";
    const int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (len <= 0) return L"";
    std::wstring w((size_t)len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), len);
    return w;
}

// ---------- 字符串/路径小工具 ----------

std::wstring trim(const std::wstring& s)
{
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == L' ' || s[b] == L'\t' || s[b] == L'\r' || s[b] == L'\n')) ++b;
    while (e > b && (s[e - 1] == L' ' || s[e - 1] == L'\t' || s[e - 1] == L'\r' || s[e - 1] == L'\n')) --e;
    return s.substr(b, e - b);
}

std::wstring trim_slash(const std::wstring& s)
{
    std::wstring r = s;
    while (r.size() > 3 && (r.back() == L'\\' || r.back() == L'/')) r.pop_back();
    return r;
}

bool iequals(const std::wstring& a, const std::wstring& b)
{
    if (a.size() != b.size()) return false;
    return _wcsnicmp(a.c_str(), b.c_str(), a.size()) == 0;
}

bool is_absolute(const std::wstring& p)
{
    if (p.size() >= 2 && p[1] == L':') return true;                        // C:\...
    if (p.size() >= 2 && p[0] == L'\\' && p[1] == L'\\') return true;      // \\server\share
    return false;
}

bool dir_exists(const std::wstring& p)
{
    const DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

// 逐级创建目录(安装 hook 之前调用, 用的是原始 Win32)
bool ensure_dir(const std::wstring& path)
{
    if (dir_exists(path)) return true;
    for (size_t i = 1; i < path.size(); ++i)
    {
        if (path[i] != L'\\' && path[i] != L'/') continue;
        CreateDirectoryW(path.substr(0, i).c_str(), nullptr);   // 已存在/无权限都忽略
    }
    CreateDirectoryW(path.c_str(), nullptr);
    return dir_exists(path);
}

// profile 名: 只允许能直接做目录名的字符。绝对路径由调用方另行放行。
bool name_is_valid(const std::wstring& name)
{
    if (name.empty() || name.size() > 64) return false;
    if (name == L"." || name == L"..") return false;
    for (const wchar_t c : name)
    {
        if (c < 0x20 || c == 0x7F) return false;
        if (c == L'<' || c == L'>' || c == L':' || c == L'"' || c == L'/' || c == L'\\' ||
            c == L'|' || c == L'?' || c == L'*') return false;
    }
    // Windows 不允许目录名以空格或点结尾
    if (name.back() == L' ' || name.back() == L'.') return false;
    return true;
}

// ---------- 从 exe 推导游戏目录 / app.info ----------

// <游戏目录>\<exe 名>_Data
bool find_data_dir(std::wstring& out)
{
    wchar_t exe[MAX_PATH] = {};
    if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) return false;

    const std::wstring exe_path = exe;
    const size_t slash = exe_path.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return false;
    const std::wstring dir = exe_path.substr(0, slash);

    std::wstring stem = exe_path.substr(slash + 1);
    const size_t dot = stem.find_last_of(L'.');
    if (dot != std::wstring::npos) stem = stem.substr(0, dot);

    const std::wstring primary = dir + L"\\" + stem + L"_Data";
    if (dir_exists(primary)) { out = primary; return true; }

    // 兜底: 扫描 <游戏目录>\*_Data(以防 exe 被改名/复制)
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW((dir + L"\\*_Data").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    bool found = false;
    do
    {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        const std::wstring cand = dir + L"\\" + fd.cFileName;
        if (GetFileAttributesW((cand + L"\\app.info").c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            out = cand;
            found = true;
            break;
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return found;
}

// app.info 内容为两行 UTF-8: 公司名 / 产品名(如 "feimo\nAstralParty_CN")
bool read_app_info(std::wstring& company, std::wstring& product)
{
    std::wstring data_dir;
    if (!find_data_dir(data_dir)) return false;

    const std::wstring file = data_dir + L"\\app.info";
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    char buf[512] = {};
    DWORD got = 0;
    const BOOL ok = ReadFile(h, buf, sizeof buf - 1, &got, nullptr);
    CloseHandle(h);
    if (!ok || got == 0) return false;

    std::string text(buf, got);
    if (text.size() >= 3 && (unsigned char)text[0] == 0xEF && (unsigned char)text[1] == 0xBB &&
        (unsigned char)text[2] == 0xBF)
        text.erase(0, 3);   // UTF-8 BOM

    const size_t nl = text.find('\n');
    if (nl == std::string::npos)
    {
        log_line("[profile] warning: app.info has no product line");
        return false;
    }

    company = trim(from_utf8(text.substr(0, nl)));
    product = trim(from_utf8(text.substr(nl + 1)));
    // 产品行后面可能还有内容(极少数版本), 只取第一行
    const size_t nl2 = product.find(L'\n');
    if (nl2 != std::wstring::npos) product = trim(product.substr(0, nl2));
    return !company.empty() && !product.empty();
}

bool local_low_root(std::wstring& out)
{
    wchar_t buf[MAX_PATH] = {};
    DWORD n = GetEnvironmentVariableW(L"USERPROFILE", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH)
    {
        out = std::wstring(buf, n) + L"\\AppData\\LocalLow";
        return true;
    }
    n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH)
    {
        const std::wstring local = std::wstring(buf, n);
        const size_t slash = local.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
        {
            out = local.substr(0, slash) + L"\\LocalLow";
            return true;
        }
    }
    return false;
}

// ---------- 缓存播种(硬链接) ----------

struct SeedStat
{
    unsigned long long files = 0;
    unsigned long long skipped = 0;
    DWORD first_error = 0;
};

// 列出目录下所有普通文件(相对 com.unity.addressables 的路径)。
// 必须在安装文件重定向**之前**调用 —— 装好后 FindFirstFileW 会被改写到 profile。
void list_files(const std::wstring& dir, const std::wstring& rel, std::vector<std::wstring>& out, int depth)
{
    if (depth > 24 || out.size() > 50000) return;

    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;

    do
    {
        const std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) continue;

        const std::wstring child_rel = rel.empty() ? name : rel + L"\\" + name;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
        {
            list_files(dir + L"\\" + name, child_rel, out, depth + 1);
            continue;
        }
        out.push_back(child_rel);
    } while (FindNextFileW(h, &fd));

    CloseHandle(h);
}

std::string fmt_seconds(unsigned long long ms)
{
    char buf[32] = {};
    std::snprintf(buf, sizeof buf, "%.1fs", (double)ms / 1000.0);
    return buf;
}

struct SeedTask
{
    std::wstring src_root;
    std::wstring dst_root;
    std::wstring marker;
    std::vector<std::wstring> files;
};

// 后台线程: 逐个建硬链接。目标已存在就跳过(上次被中断时可续传),
// 全部成功才写完成标记 —— 之后启动不再遍历, 也不会把玩家主动清掉的缓存链回来。
DWORD WINAPI seed_thread(LPVOID param)
{
    std::unique_ptr<SeedTask> task(static_cast<SeedTask*>(param));
    if (!task) return 0;

    const ULONGLONG t0 = GetTickCount64();
    SeedStat st;
    std::wstring last_dir;

    for (const std::wstring& rel : task->files)
    {
        const std::wstring src = task->src_root + L"\\" + rel;
        const std::wstring dst = task->dst_root + L"\\" + rel;

        const size_t slash = rel.find_last_of(L'\\');
        const std::wstring dir =
            slash == std::wstring::npos ? task->dst_root : task->dst_root + L"\\" + rel.substr(0, slash);
        if (dir != last_dir)
        {
            ensure_dir(dir);
            last_dir = dir;
        }

        if (GetFileAttributesW(dst.c_str()) != INVALID_FILE_ATTRIBUTES) { ++st.files; continue; }
        if (CreateHardLinkW(dst.c_str(), src.c_str(), nullptr)) { ++st.files; continue; }
        const DWORD err = GetLastError();
        if (err == ERROR_ALREADY_EXISTS || err == ERROR_FILE_EXISTS) { ++st.files; continue; }
        ++st.skipped;
        if (st.first_error == 0) st.first_error = err;
    }

    std::string line = "[profile] seeded com.unity.addressables: " + std::to_string(st.files) + " files linked";
    if (st.skipped > 0)
        line += " (" + std::to_string(st.skipped) + " failed, first error " + std::to_string(st.first_error) + ")";
    line += " in " + fmt_seconds(GetTickCount64() - t0);
    log_line(line);

    if (st.skipped == 0)
    {
        HANDLE h = CreateFileW(task->marker.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                               FILE_ATTRIBUTE_HIDDEN, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
    return 0;
}

} // namespace

namespace cesium::profile
{

bool prepare(const std::string& name_from_config, ProfilePaths& out)
{
    out = ProfilePaths{};

    const std::wstring name = trim(from_utf8(name_from_config));
    if (name.empty())
    {
        log_line("[profile] none selected: the game keeps writing its own save folder");
        return true;
    }

    std::wstring company, product;
    if (!read_app_info(company, product))
    {
        log_line("[profile] warning: cannot read app.info next to the game exe; profile disabled");
        return false;
    }

    std::wstring low;
    if (!local_low_root(low))
    {
        log_line("[profile] warning: cannot resolve the LocalLow folder (%USERPROFILE%); profile disabled");
        return false;
    }

    const std::wstring source = low + L"\\" + company + L"\\" + product;
    std::wstring target;

    if (is_absolute(name))
    {
        // 高级用法: profile 直接指向一个绝对路径(可以把账号放到别的盘)
        target = trim_slash(name);
    }
    else
    {
        if (!name_is_valid(name))
        {
            log_line("[profile] warning: invalid profile name \"" + to_utf8(name) +
                     "\" (letters, digits, '_', '-', '.'; at most 64 chars)");
            return false;
        }
        target = source + L"_profiles\\" + name;
    }

    if (iequals(trim_slash(source), target))
    {
        log_line("[profile] warning: profile folder must differ from the game's own folder; profile disabled");
        return false;
    }
    if (target.size() > source.size() && _wcsnicmp(target.c_str(), source.c_str(), source.size()) == 0 &&
        (target[source.size()] == L'\\' || target[source.size()] == L'/'))
    {
        log_line("[profile] warning: profile folder must not be inside the game's own folder; profile disabled");
        return false;
    }

    if (!ensure_dir(target))
    {
        log_line("[profile] warning: cannot create \"" + to_utf8(target) +
                 "\" (error " + std::to_string(GetLastError()) + "); profile disabled");
        return false;
    }

    // 首次新建 profile: 需要把 Unity 下载缓存(约 7 GB)硬链接播种过来, 否则新 profile
    // 启动时要重新下载。这里只**列清单**(装好重定向后 FindFirstFileW 会被改写, 必须提前列),
    // 真正的建链接放在 start_seed 的后台线程 —— 不拖慢引导线程的 mod 加载。
    // 只播种这一个目录: 账号令牌 / SaveFile.es3 属于 profile 私有数据, 不能共用。
    const std::wstring marker = target + L"\\.seeded";
    const std::wstring cache_src = source + L"\\com.unity.addressables";
    if (GetFileAttributesW(marker.c_str()) == INVALID_FILE_ATTRIBUTES && dir_exists(cache_src))
    {
        list_files(cache_src, L"", out.seed_files, 0);
        if (!out.seed_files.empty())
            log_line("[profile] first run of this profile: linking " + std::to_string(out.seed_files.size()) +
                     " cached bundles from the main folder");
    }

    out.active = true;
    out.name = name;
    out.source_dir = source;
    out.target_dir = target;

    log_line("[profile] active \"" + to_utf8(name) + "\"");
    log_line("[profile]   game folder    " + to_utf8(source));
    log_line("[profile]   profile folder " + to_utf8(target));
    return true;
}

void start_seed(ProfilePaths& paths)
{
    if (!paths.active || paths.seed_files.empty()) return;

    auto* task = new SeedTask;
    task->src_root = paths.source_dir + L"\\com.unity.addressables";
    task->dst_root = paths.target_dir + L"\\com.unity.addressables";
    task->marker = paths.target_dir + L"\\.seeded";
    task->files = std::move(paths.seed_files);
    paths.seed_files.clear();

    ensure_dir(task->dst_root);

    HANDLE h = CreateThread(nullptr, 0, seed_thread, task, 0, nullptr);
    if (!h)
    {
        delete task;
        log_line("[profile] warning: cannot start the cache seeding thread; this profile will re-download bundles");
        return;
    }
    CloseHandle(h);
}

} // namespace cesium::profile

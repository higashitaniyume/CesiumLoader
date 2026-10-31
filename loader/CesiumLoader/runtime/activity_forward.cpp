// activity_forward.cpp - 活动日志转发线程(activity-mod-<会话>.log -> 控制台)
//
// 从 loader.cpp 拆出(纯移动)。线程常驻, 是否真正转发由 forwardActivityLog 开关控制
// (运行期状态见 runtime/state.h), 这样 shell 里可以随时开关而不用重启线程。

#include "runtime/loader_internal.h"

#include "logging/logging.h"
#include "platform/loader.h"
#include "runtime/state.h"

#include <string>
#include <vector>

// ---------- 日志转发线程(activity-mod-<会话>.log -> 控制台) ----------

// mod / bootstrap 无法直接 P/Invoke 到自定义导出(IL2CPP 限制), 改为写文件,
// 本线程监控本次启动的 activity 日志, 把新增行写到控制台窗口。
//
// 日志行由托管侧(SdkLog)写成 "[时间] [级别] [标签] 正文", 这里按级别给正文上色,
// 时间戳与级别留给前缀(置暗):
//   DBG 暗灰 / INF 默认色 / WRN 黄 / ERR·FTL 红
// 级别是固定 3 个字母的 ASCII 大写; 格式不符的行(旧日志、手工追加)按原样输出。
static bool split_activity_line(const std::string& line, std::string& prefix,
                                std::string& body, WORD& body_color)
{
    // "[12:34:56.789] [INF] ..." —— 时间戳不超过 16 字符, 级别固定 3 字母。
    if (line.size() < 12 || line[0] != '[') return false;
    const size_t time_end = line.find(']');
    if (time_end == std::string::npos || time_end < 4 || time_end > 16) return false;
    if (time_end + 7 >= line.size()) return false;
    if (line[time_end + 1] != ' ' || line[time_end + 2] != '[') return false;
    if (line[time_end + 6] != ']' || line[time_end + 7] != ' ') return false;

    const std::string level = line.substr(time_end + 3, 3);
    WORD color = 0;
    if (level == "DBG") color = LOG_DIM;
    else if (level == "INF") color = LOG_DEFAULT;
    else if (level == "WRN") color = LOG_YELLOW;
    else if (level == "ERR" || level == "FTL") color = LOG_RED;
    else return false;

    prefix = line.substr(0, time_end + 8);   // 含级别后的空格
    body = line.substr(time_end + 8);
    body_color = color;
    return true;
}

namespace cesium::runtime::forward
{

static DWORD WINAPI forward_activity_log(LPVOID param)
{
    std::wstring log_dir = *reinterpret_cast<std::wstring*>(param);
    delete reinterpret_cast<std::wstring*>(param);

    // 必须用当前这次启动的文件名(带会话标识), 不能写死 activity-mod.log —— 否则会去 tail
    // 上一次启动留下的旧文件, 控制台什么都看不到。
    const std::wstring path = cesium::log::activity_log_path(log_dir);
    LARGE_INTEGER last_len{};
    last_len.QuadPart = 0;
    // 初始:跳过已有内容(只转发"从现在起"的新日志)
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad))
    {
        LARGE_INTEGER size;
        size.HighPart = fad.nFileSizeHigh;
        size.LowPart = fad.nFileSizeLow;
        last_len = size;
    }

    for (;;)
    {
        Sleep(150);
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) continue;
        LARGE_INTEGER cur;
        cur.HighPart = fad.nFileSizeHigh;
        cur.LowPart = fad.nFileSizeLow;

        // 转发可以被 shell 的 set loader.forwardActivityLog 关掉。关闭期间仍要跟上文件位置,
        // 否则重新打开时会把关闭期间积累的内容一次性倒到控制台上。
        if (!cesium::runtime::forward_enabled())
        {
            if (cur.QuadPart > last_len.QuadPart) last_len = cur;
            continue;
        }

        if (cur.QuadPart <= last_len.QuadPart) continue;

        HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) continue;
        LARGE_INTEGER offset = last_len;
        SetFilePointerEx(f, offset, nullptr, FILE_BEGIN);
        std::vector<char> buf((size_t)(cur.QuadPart - last_len.QuadPart));
        DWORD read = 0;
        if (ReadFile(f, buf.data(), (DWORD)buf.size(), &read, nullptr))
        {
            // 按行转发
            std::string text(buf.data(), read);
            size_t start = 0;
            while (start < text.size())
            {
                size_t end = text.find('\n', start);
                std::string line = (end == std::string::npos)
                    ? text.substr(start) : text.substr(start, end - start);
                // 去 \r
                while (!line.empty() && (line.back() == '\r')) line.pop_back();
                // 去首尾空白
                size_t b = line.find_first_not_of(" \t\r\n");
                if (b != std::string::npos)
                {
                    const std::string trimmed = line.substr(b);
                    std::string prefix, body;
                    WORD color = LOG_DEFAULT;
                    if (split_activity_line(trimmed, prefix, body, color))
                        console_write_styled(prefix.c_str(), body.c_str(), LOG_DIM, color);
                    else
                        console_write(trimmed.c_str());
                }
                if (end == std::string::npos) break;
                start = end + 1;
            }
        }
        CloseHandle(f);
        last_len = cur;
    }
    return 0;
}

bool start_activity_forward_thread()
{
    auto* log_dir_ptr = new std::wstring(logs_dir());
    HANDLE h = CreateThread(nullptr, 0, forward_activity_log, log_dir_ptr, 0, nullptr);
    if (h) { CloseHandle(h); return true; }
    delete log_dir_ptr;
    return false;
}

} // namespace cesium::runtime::forward

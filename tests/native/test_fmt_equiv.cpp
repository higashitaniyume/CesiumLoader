// test_fmt_equiv.cpp - P1 等价性验证
//
// 把 steamhack.cpp / speedctl.cpp 里用 printf 家族写的格式化换成 fmt::format 之后,
// 输出必须**逐字节一致**。本测试对每个替换点用原实现与现实现各跑一遍并比对。
//
// 编译运行: powershell -File tests\native\run-native-tests.ps1

#include <fmt/format.h>

#include <cstdint>
#include <cstdio>
#include <iostream>
#include <string>
#include <vector>

namespace
{
int g_fail = 0;
int g_total = 0;

void check(const std::string& what, const std::string& old_s, const std::string& new_s)
{
    ++g_total;
    if (old_s == new_s) return;
    ++g_fail;
    std::cout << "  [FAIL] " << what << "\n"
              << "         printf = \"" << old_s << "\"\n"
              << "         fmt    = \"" << new_s << "\"\n";
}
} // namespace

// ===== steamhack.cpp: hex_str(uintptr_t) —— 原 "0x%llX" =====
static std::string old_hex_str(uintptr_t v)
{
    char buf[32];
    sprintf_s(buf, sizeof(buf), "0x%llX", (unsigned long long)v);
    return buf;
}
static std::string new_hex_str(uintptr_t v)
{
    return fmt::format("0x{:X}", static_cast<unsigned long long>(v));
}

// ===== steamhack.cpp: hex_code_str(unsigned long) —— 原 "0x%08lX" =====
static std::string old_hex_code(unsigned long c)
{
    char buf[32];
    sprintf_s(buf, sizeof(buf), "0x%08lX", c);
    return buf;
}
static std::string new_hex_code(unsigned long c)
{
    return fmt::format("0x{:08X}", c);
}

// ===== speedctl.cpp: fmt_speed(double) —— 原 "%.3f" =====
static std::string old_speed(double v)
{
    char buf[32];
    _snprintf_s(buf, sizeof(buf), _TRUNCATE, "%.3f", v);
    return std::string(buf);
}
static std::string new_speed(double v)
{
    return fmt::format("{:.3f}", v);
}

int main()
{
    std::cout << "== P1: fmt vs printf 等价性 ==\n";

    // hex_str: 覆盖 0 / 小值 / 典型 RVA / 64 位高位 / 最大值
    const uintptr_t addrs[] = {
        0, 0x1, 0x8330, 0x9BC180, 0x765530, 0x7FFC84FCE400ULL,
        0xFFFFFFFF, 0x100000000ULL, 0xFFFFFFFFFFFFFFFFULL,
    };
    for (uintptr_t a : addrs)
        check("hex_str(" + std::to_string(static_cast<unsigned long long>(a)) + ")",
              old_hex_str(a), new_hex_str(a));

    // hex_code_str: 覆盖 0 / 不足 8 位补零 / 满 8 位 / 高位溢出(unsigned long 是 32 位)
    const unsigned long codes[] = { 0, 1, 0xABC, 0xC0000005, 0x12345678, 0xFFFFFFFF };
    for (unsigned long c : codes)
        check("hex_code_str(" + std::to_string(c) + ")", old_hex_code(c), new_hex_code(c));

    // fmt_speed: 覆盖全部可能的倍率值(1.0~100, mod 会发的档位) + 边界/异常
    std::vector<double> speeds = { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0, 10.0,
                                   33.333, 66.666, 99.999, 100.0,
                                   0.999, 1.0005, 2.0005, 0.0, -1.0, 123.4567 };
    for (double s : speeds)
        check("fmt_speed(" + new_speed(s) + ")", old_speed(s), new_speed(s));

    // 额外: 确认小数点与系统区域无关(fmt 固定 '.')
    check("locale 无关于小数点", old_speed(1.5), "1.500");

    std::cout << "  用例: " << g_total << "  失败: " << g_fail << "\n";
    if (g_fail == 0) std::cout << "== 全部通过 ==\n";
    return g_fail == 0 ? 0 : 1;
}

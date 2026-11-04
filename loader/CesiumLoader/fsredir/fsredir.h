// fsredir.h - 进程内文件 API 前缀重定向(多 profile 的实现手段)
//
// 把游戏对 <LocalLow>\<company>\<product> 的读写改写到 profile 目录:
//   CreateFileW / CreateDirectoryW / RemoveDirectoryW / DeleteFileW / SetFileAttributesW /
//   GetFileAttributesW(Ex) / FindFirstFileW(Ex) / MoveFileW(Ex) / CopyFileW / CopyFileExW / ReplaceFileW
//
// CopyFileExW 不能漏: 游戏的托管 System.IO.File.Copy 由 il2cpp 实现, 走的是 CopyFileExW
// (GameAssembly.dll 的导入表), 只挂 CopyFileW 会让"删除备份 + 复制备份"里的复制落回原目录。
//
// 只在本进程内生效, 游戏文件一个字节都不复制; 同一台机器上的多个实例各装各的
// (每个进程只指向自己的 profile)。
//
// 匹配规则(实测约束):
//   - 大小写不敏感;
//   - 正反斜杠都认(BnSdk 日志里打印的是正斜杠路径);
//   - 识别 \\?\ 与 \??\ 长路径前缀并原样保留;
//   - 相对路径 / UNC / 设备路径一律放过(一律不改写)。
//
// 调用方注意: install() 必须在游戏开始读写账号文件之前调用(引导线程里
// Sleep(1500) 之后即可 —— 实测游戏自身的文件访问都在 il2cpp 就绪后, ≥5s)。

#pragma once

#include <string>

namespace cesium::fsredir
{

// 安装重定向: source_dir -> target_dir(两者都是不带尾随分隔符的绝对路径)。
// 幂等; 失败记日志并返回 false(调用方应让游戏照常运行, 只是没有 profile 隔离)。
bool install(const std::wstring& source_dir, const std::wstring& target_dir);

// 是否已装好并生效。
bool active();

} // namespace cesium::fsredir

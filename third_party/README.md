# third_party — 随仓库入库的第三方依赖 (vendored)

这里的依赖都是**源码入库**的：克隆下来直接 `msbuild` 就能编译，**不需要任何包管理器**
（本机、CI runner 都一样）。

以前这几个库是用 **vcpkg manifest 模式**拉的（`vcpkg.json` + `builtin-baseline`）。问题在于
命令行 `msbuild` 并不会自己去发现 vcpkg —— 本机之所以能编译，是因为 `vcpkg integrate install`
往用户目录写了 `vcpkg.user.targets/props`；而 CI runner 是全新用户目录，于是 CI 直接报
`Cannot open include file: 'nlohmann/json.hpp'`。改成源码入库后，构建不再依赖任何本地环境状态。

| 库 | 版本 | 用途 | 用法 | 许可证 |
|---|---|---|---|---|
| [fmt](https://github.com/fmtlib/fmt) | 12.2.0 | 字符串格式化 | **header-only**（`FMT_HEADER_ONLY`），不需要任何 .lib | MIT |
| [spdlog](https://github.com/gabime/spdlog) | 1.17.0 | 日志 | **header-only**（不定义 `SPDLOG_COMPILED_LIB`），使用外部 fmt | MIT |
| [nlohmann/json](https://github.com/nlohmann/json) | 3.12.0 | JSON / JSONC 解析 | 单头文件 | MIT |
| [MinHook](https://github.com/TsudaKageyu/minhook) | 上游未标注版本 | inline hook（变速引擎） | 源码编译（`minhook/src/*.c`） | BSD-2-Clause |

各库的许可证全文在各自目录下（`LICENSE.txt`；MinHook 的声明在源码文件头部）—— **不要删除**。

## 为什么是「源码入库」而不是子模块 / 包管理器

- 本仓库的产物是**注入游戏进程的代理 `version.dll`**：所有依赖必须是静态或内联的，
  绝不能要求额外分发 `fmt.dll` / `spdlog.dll`。vendored 头文件天然满足。
- 入库的只有真正需要的东西（头文件 + MinHook 的 `.c`），没有构建系统、没有外部下载，
  构建行为完全由仓库内容决定。
- 体积可控：三个 header-only 库合计约 1.9 MB。

## 两个宏必须全工程一致 ⚠️

| 宏 | 含义 |
|---|---|
| `FMT_HEADER_ONLY` | fmt 走 header-only，不链接任何 fmt 库 |
| `SPDLOG_FMT_EXTERNAL` | spdlog 用**我们这份** fmt，而不是它自带的 `spdlog/fmt/bundled` |

`CesiumLoader.vcxproj` 的 `PreprocessorDefinitions` 里定义了这两个宏；原生测试脚本
`tests\native\run-native-tests.ps1` 用 `/D` 传同样的宏。**两边必须一致** —— 同一个二进制里
若有一部分 TU 按不同方式使用 fmt，就是 ODR / 对象布局冲突，表现为静默的内存损坏。
（spdlog 自己的 `tweakme.h` 里也写死了 `SPDLOG_FMT_EXTERNAL`，见该文件注释。）

## 怎么升级某个库

1. 取该版本的头文件：上游 release，或从 `vcpkg install` 出来的
   `vcpkg_installed/x64-windows-static-md/include/`（所见即所用）；
2. 覆盖对应 `include/` 目录下的内容；
3. 更新本文件表格里的版本号；
4. 重新编译，并跑原生测试与冒烟测试：
   `powershell -File tests\native\run-native-tests.ps1`、`tools\smoke\` 下的冒烟脚本。

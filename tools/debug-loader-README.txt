cesium-loader-debug-{{VERSION}}

这是 CesiumLoader 的【Debug 版加载器】包，给开发者排查原生加载器问题时使用，不建议日常游玩安装。

与正式包的区别:
- version.dll 是 Debug 配置构建: 未优化、带完整调试信息，可在 Visual Studio 里逐行单步。
- 依赖 Visual C++ 调试运行时(ucrtbased.dll / vcruntime140d.dll / MSVCP140D.dll)，
  只有装了 Visual Studio(含「使用 C++ 的桌面开发」)的机器才能加载它；普通玩家请用正式包。
- version.pdb 与 dll 同目录: 首次断点 VS 会询问源码位置，选本仓库
  modding\msvc\loader\CesiumLoader\ 下的对应文件即可(CI 构建记录的不是你本机的路径)。
- 其余目录(sdk / bootstrap / mods / 配置 / 说明)与正式包完全一致。

使用:
1. 解压到游戏 exe 目录(与正式包同布局；已有正式包时直接覆盖 version.dll + version.pdb 即可)。
2. 想让加载器在启动时停下来等调试器: 把 AstralParty_ModLoader\doorstop_config.json 里的
   "debugWaitForDebugger" 改成 true(默认 false)，然后启动游戏并立即用 VS 附加:
   调试 → 附加到进程 → AstralParty_CN.exe → 代码类型选 Native。
3. 排查完把 debugWaitForDebugger 改回 false，或换回正式包的 version.dll。

崩溃 / 卡死取证:
- 致命异常与 hang dump 都写在 AstralParty_ModLoader\crash\ 下(.dmp + 同名 .txt)。
- 控制台 Debug Shell 里输入 dump 可随时写一份线程栈转储。
- 验收: 在仓库根执行 pwsh -NoProfile -File modding\msvc\tools\verify-crash-dump.ps1

⚠ Debug 版比正式版慢(未优化 + 调试 CRT)，只用于复现与定位问题。

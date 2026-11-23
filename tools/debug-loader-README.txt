cesium-loader-debug-{{VERSION}}

这是 CesiumLoader 的【Debug 版加载器】包，给开发者排查原生加载器问题时使用，不建议日常游玩安装。

与正式包的区别:
- version.dll(代理) 与 CesiumLoader.dll(加载器核心) 都是 Debug 配置构建: 未优化、带完整调试
  信息，可在 Visual Studio 里逐行单步。两个文件的符号一一对应，必须成对使用。
- 依赖 Visual C++ 调试运行时(ucrtbased.dll / vcruntime140d.dll / MSVCP140D.dll)，
  只有装了 Visual Studio(含「使用 C++ 的桌面开发」)的机器才能加载它；普通玩家请用正式包。
- version.pdb / CesiumLoader.pdb 与各自的 dll 同目录: 首次断点 VS 会询问源码位置，选本仓库
  modding\msvc\loader\CesiumLoader.Proxy\ (代理) 或 modding\msvc\loader\CesiumLoader\ (核心)
  下的对应文件即可(CI 构建记录的不是你本机的路径)。
- 其余目录(sdk / bootstrap / mods / 配置 / 说明)与正式包完全一致。

安装(两种任选):
1. AstralParty.Toys「模组与工具 → 加载器 → 选择 CesiumLoader 发布包」直接选这个 zip:
   包里带一份用 Debug dll/pdb 哈希生成的 cesium-loader.json(channel=debug)，
   和正式包走同一套校验，会被识别为"已安装"。注意:
   - 版本号与正式包相同，所以"检查更新"不会把正式版装回来；想回正式版请用 Toys 的
     版本列表或重新导入正式包。
   - Toys 只解压两个 dll / 两个 pdb / sdk / bootstrap / mods / 配置，
     不会解压本 README.txt。
2. 或手动解压到游戏 exe 目录(与正式包同布局；已有正式包时覆盖 version.dll + version.pdb +
   CesiumLoader.dll + CesiumLoader.pdb 即可)。

为什么是两个 DLL:
- version.dll 只是薄代理: 转发系统 version.dll 的导出，并在后台线程里加载核心；
- CesiumLoader.dll 是加载器主体(配置 / 变速 / 文件重定向 / profile / shell / 崩溃转储 / IL2CPP 桥)。
- 两个必须同目录: 代理按自身模块目录去找核心。缺核心或版本不匹配时游戏照常启动、只是模组
  不加载，原因写在 AstralParty_ModLoader\logs\proxy-*.log。

让加载器等调试器:
1. 把 AstralParty_ModLoader\doorstop_config.json 里的 "debugWaitForDebugger" 改成 true(默认 false)。
2. 启动游戏并立即用 VS 附加: 调试 → 附加到进程 → AstralParty_CN.exe → 代码类型选 Native。
   想断在最早的引导阶段，可以对 CesiumLoaderStart / load_core_once 下断点。
3. 排查完把 debugWaitForDebugger 改回 false，或换回正式包的两个 dll。

崩溃 / 卡死取证:
- 致命异常与 hang dump 都写在 AstralParty_ModLoader\crash\ 下(.dmp + 同名 .txt)。
- 控制台 Debug Shell 里输入 dump 可随时写一份线程栈转储。
- 验收: 在仓库根执行 pwsh -NoProfile -File modding\msvc\tools\verify-crash-dump.ps1

⚠ Debug 版比正式版慢(未优化 + 调试 CRT)，只用于复现与定位问题。

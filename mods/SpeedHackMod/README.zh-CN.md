# SpeedHackMod 2.5.7

基于 CesiumLoader `modloader-3.5.1` 的 SpeedHackMod 修改，原作者为 CesiumLoader / Yume Higashitani。
原始实现基于 `modloader-3.5.1`；本次提交已对齐上游 master，保留原作者版权和许可。

## 本次结果

验收以用户同一设备、同一场景的**游戏加加 FPS 和实际动画体验**为准。
内部 `Time.frameCount` 的统计不能替代该标准。

- 用户提供的基线：关闭加速约 500 FPS，原生 4 倍速约 100 FPS。
- Unity 模式改动后：用户实测约 200–300 FPS。
- 最终 2.5.4：用户确认出牌动画已加快、效果很好；此前也已确认技能、大厅背景、战斗入场和战斗内 1–6 点骰点动画的加速。
- 快捷键保持原有配置（默认 Delete；本机实测使用 LeftControl），可正常在 1 倍和记忆倍率间切换。

这些是本机用户实测，不能作为其他设备、画质或所有场景的帧率保证。

## 改动原理

2.5.7 固定以 Unity 的 Time.timeScale 控制游戏时间。有原生控制端口时，先确认原生时钟为 1 倍。
检测到其他工具改变原生倍率后，撤销 Unity 倍率与固定步长优化，再请求原生恢复 1 倍。

同时适配游戏中独立计时的动画，避免只改变 timeScale 导致大量动画不加速：

- FairyGUI：同步界面转场和序列帧；覆盖主界面以及不在 GRoot 下的世界空间 UIPanel，包含战斗骰点面板。
- 临时转场：从正在播放的 TweenManager 找到所属 Transition，及时同步刚启动的出牌动画；不 Seek、不 Kill，也不主动触发完成回调。
- Animator、Timeline、Spine：只将原本使用独立时间的通道改为跟随游戏时间，避免对已跟随 timeScale 的动画再次叠加倍率。
- Cinemachine：同步独立计时的镜头切换，覆盖战斗入场的相应演出。
- CRI：覆盖 GGraph 和两类战斗 Quad。游戏在开始播放后可能重写倍率，播放器也会复用。调度器约每 0.25 秒巡检每类活跃组件，等待 Playing 和可读的 movieInfo，重新应用静音视频倍率，最高 3x。
- 战斗 Quad 按游戏的原始速率乘以目标倍率。Timeline 同步全屏视频由游戏调度。含音轨视频保持游戏速率，CRI 不支持在这类视频播放中直接改速。
- 运行时确认 CRI maxFrameDrop 枚举包含游戏使用的值 10 后，才提高较低上限。关闭加速时恢复仍可安全访问且由本 mod 持有的属性。可通过 CriMovieManager 的状态回调识别的战斗 UI 使用并恢复 BattleConfig.RoleAnimatorSpeed。其他 UI 使用默认 1x，战斗 Quad 使用巡检时读取的游戏速率。
- 在活动日志 activity-mod-*.log 中搜索 CRI 视频通道、静音 Playing、SkippedAudioVideos 或 Timeline。跳过统计是限流后的观测次数。短于巡检窗口的视频仍可能错过，实际速度也受解码和渲染负载影响。
- Windows 有有效 CESIUM_SPEED_PORT 且握手尚未完成时，等待 SDK 回执。无有效端口的设备可直接使用 Unity 变速。

Unity 变速启用时的固定步长：

```text
原生时钟倍率 = 1
Unity timeScale = 游戏原始 timeScale × 目标倍率
fixedDeltaTime = min(原始固定步长 × 目标倍率, maximumDeltaTime)
```

固定步长变大可减少加速后的物理更新需求，但可能降低高速下的物理精度。
不能将固定更新次数的减少比例直接当作帧率提升比例。

## 控制开销

- 原生倍率请求和状态确认使用 `CESIUM_SPEED_PORT` 指定的本进程回环 UDP 端口，不再读写共享通信文件；SDK 负责有界收包、丢包重试、回执去重及过期检查。
- 运行中不每帧反复设置 timeScale 或固定步长。
- 类型和反射成员缓存；尚未加载的可选动画类型每 5 秒重试一次。场景查询按类型错开。CRI 排除非激活对象，其他动画通道保留对象池发现行为。
- 每帧最多执行一次到期的组件类型查询，随后最多处理 512 个结果；不同通道使用 0.25–1 秒的巡检间隔。
- UI 树增量处理，每帧最多 64 个遍历步骤；新窗口优先。
- 活跃转场最多检查 128 个 Tween，并缓存目标与所属转场；不重新扫描整个场景。
- 缓存数量有上限，关闭加速或卸载时清理持有状态。被销毁的组件只跳过，不每帧输出异常。
- 正常运行只记录状态变化和首次发现通道，不持续采样 FPS。

本 mod 不修改游戏加加的配置、进程、显示数据，也不削减画质或修改帧率上限。

## 配置

启动和热重载时，模组从 config.json 清理旧版 accelerationMode 及其说明，原有倍率、按键和连按设置继续可用。
optimizeFixedUpdates、accelerateUiAnimations、accelerateIndependentAnimations 固定启用，旧配置中的这三个字段及说明也会被清理。

原有倍率、按键、连按设置和配置保存功能保留。2.4.2 的状态延迟修复、按住防重入也保留。
`repeatInterval=0` 时每次按住只调整一次，松开后可再次调整。热重载倍率上下限会约束正在使用的加速倍率，临时切回的 1 倍不会因此重新开启加速。
回到 1 倍或卸载时恢复仍由本 mod 持有的属性；不会主动恢复已被其他代码改成不同值的动画模式。
不会主动解除游戏暂停；暂停期间撤销动画接管并保留 timeScale 的原始基准，恢复保存的 timeScale 时不会重复相乘。

配合 TurnClockMod 时，可识别的缩放时间计时器按原生倍率与 Unity 当前帧时间缩放的组合补偿，实时计时器只补偿原生倍率；无法识别时钟类型的计时器保留原生补偿方式。详见[回合时钟说明](../TurnClockMod/README.zh-CN.md)。

## 构建、启用与回退

```powershell
dotnet build mods/SpeedHackMod/SpeedHackMod.csproj -c Release -p:NuGetAudit=false
dotnet test tests/SpeedHackMod.Tests/SpeedHackMod.Tests.csproj -c Release -p:NuGetAudit=false
```

构建产物位于 `mods/SpeedHackMod/bin/Release/netstandard2.0/`。
退出游戏后将生成的 `SpeedHackMod.dll`、PDB 和清单替换到 mod 目录，保留自己的 `config.json`。
本版本应同时更新配套原生加载器与 SDK；旧文件通道不作为 UDP 的备用通道。Unity 变速的倒计时补偿需要搭配更新后的 TurnClockMod。模组及加载器配置无需替换。

回退到 2.5.6 时，可在旧版配置中补回 accelerationMode 并设为 unity。旧版缺少该字段时默认使用原生变速。

## 2.5.6 验证范围

Mono.Cecil 从 AstralParty.Runtime.dll 核实了 CriMovieManager 使用系统计时器、GGraph 控制器复用，以及 FightWindow 在异步播放返回后重写速率。两类战斗 Quad 都持有 CRI Asset 控制器，自定义 Quad 使用 speed，全屏 Quad 在非 Timeline 分支使用 _trackSpeed。两个 Quad 的 Awake 设置丢帧上限 0，UI 管理器设置 2，全屏 Timeline 分支设置 10。

安装的 SDK 3.8.10 与仓库的 AstralParty.Runtime、UnityEngine.CoreModule 用于验证编译产物的直接引用。当前安装为 IL2CPP，未找到独立的 CriMw 托管 DLL。反射接口的实际运行、解码提速、主页背景切换和帧率仍需游戏内验证。启动日志只能确认原生时钟回到 1x，具体视频命中要查看活动日志。

## 2.5.4 历史验证

- Release 编译成功，0 错误、0 警告。
- 43 项控制器和调度检查通过，覆盖异步确认、避免双重倍率、暂停恢复、模式切换、世界空间骰点 UI、镜头模式、静音视频上限和音轨跳过、短出牌转场及时同步、属性恢复及工作量限制。
- 本机日志确认原生时钟 1 倍、Unity timeScale 4，相关通道已发现并同步。
- 用户最终确认 2.5.4 的出牌加速和游戏加加帧率效果。
- 临时 FPS 诊断组件不在包内，安装目录中也已移出；最终版不持续记录 FPS。

## 参考和许可

Unity 的 timeScale 不会改变 unscaledDeltaTime，因此必须分别处理独立计时通道：
[Unity timeScale](https://docs.unity3d.com/ja/current/ScriptReference/Time-timeScale.html)、
[AnimatorUpdateMode](https://docs.unity3d.com/ja/current/ScriptReference/AnimatorUpdateMode.html)、
[DirectorUpdateMode](https://docs.unity3d.com/ja/6000.0/ScriptReference/Playables.DirectorUpdateMode.html)。

视频范围和限制见 [CRI Player.SetSpeed 官方文档](https://game.criware.jp/manual/unity_plugin_en/latest/contents/classCriWare_1_1CriMana_1_1Player.html)。
FairyGUI 的具体接入依据本机游戏程序集核对；不同游戏版本的字段或动画实现变化可能需要调整。

保留 CesiumLoader 原版权和 NOTICE，按 AGPL-3.0 分发；完整许可证随包提供。
上游：https://github.com/higashitaniyume/CesiumLoader

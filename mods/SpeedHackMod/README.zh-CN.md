# SpeedHackMod 2.5.4 性能改进版

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

原版通过加载器缩放进程看到的系统时间。新增 `unity` 模式先请求原生时钟恢复 1 倍，
收到状态确认后，再以 Unity `Time.timeScale` 控制游戏时间，减少全局计时受影响的范围。
确认前不启用 Unity 倍率。检测到其他工具改变原生倍率后，撤销 Unity 倍率与固定步长优化，再请求原生恢复 1 倍。
保留 `native` 模式作为兼容选项。

同时适配游戏中独立计时的动画，避免只改变 timeScale 导致大量动画不加速：

- FairyGUI：同步界面转场和序列帧；覆盖主界面以及不在 GRoot 下的世界空间 UIPanel，包含战斗骰点面板。
- 临时转场：从正在播放的 TweenManager 找到所属 Transition，及时同步刚启动的出牌动画；不 Seek、不 Kill，也不主动触发完成回调。
- Animator、Timeline、Spine：只将原本使用独立时间的通道改为跟随游戏时间，避免对已跟随 timeScale 的动画再次叠加倍率。
- Cinemachine：同步独立计时的镜头切换，覆盖战斗入场的相应演出。
- CRI：同步已知 UI 控制器中的静音视频，按官方接口上限最高 3 倍；包含音轨或音轨信息未知的视频不强行改速。

CRI 视频与 Unity 动画的倍率上限不同，所以选择 4 倍速时，适配的静音 UI 视频最高为 3 倍。
不接管具有自定义轨道速度的全屏 Timeline 视频控制器。关闭时，适配的 UI 视频回到游戏通常使用的 1 倍；
如果其他工具另行设置了视频倍率，不能保证恢复该外部值。

Unity 模式下开启固定更新优化时：

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
- 类型和反射成员缓存；尚未加载的可选动画类型每 5 秒重试一次。场景组件查询按类型错开，只查场景对象，包含已实例化的非激活对象以覆盖对象池。
- 每帧最多执行一次到期的组件类型查询，随后最多处理 512 个结果；不同通道使用 0.25–1 秒的巡检间隔。
- UI 树增量处理，每帧最多 64 个遍历步骤；新窗口优先。
- 活跃转场最多检查 128 个 Tween，并缓存目标与所属转场；不重新扫描整个场景。
- 缓存数量有上限，关闭加速或卸载时清理持有状态。被销毁的组件只跳过，不每帧输出异常。
- 正常运行只记录状态变化和首次发现通道，不持续采样 FPS。

本 mod 不修改游戏加加的配置、进程、显示数据，也不削减画质或修改帧率上限。

## 配置

```json
{
  "accelerationMode": "unity",
  "optimizeFixedUpdates": true,
  "accelerateUiAnimations": true,
  "accelerateIndependentAnimations": true
}
```

以上是新增或相关字段示例，不要用它替换整个配置文件。

- `accelerationMode`：`unity` 使用新模式，`native` 使用原有系统时钟加速。为兼容未选择新模式的安装，缺少字段时仍默认 `native`；本机已设为 `unity`。
- `optimizeFixedUpdates`：是否按倍率调整固定更新步长；物理行为异常时可关闭。
- `accelerateUiAnimations`：是否同步 FairyGUI 动画。
- `accelerateIndependentAnimations`：是否发现并同步独立计时的场景组件、静音 UI 视频和世界空间面板。

原有倍率、按键、连按设置和配置保存功能保留。2.4.2 的状态延迟修复、按住防重入也保留。
`repeatInterval=0` 时每次按住只调整一次，松开后可再次调整。热重载倍率上下限会约束正在使用的加速倍率，临时切回的 1 倍不会因此重新开启加速。
切换模式、回到 1 倍或卸载时恢复仍由本 mod 持有的属性；不会主动恢复已被其他代码改成不同值的动画模式。
不会主动解除游戏暂停；暂停期间撤销动画接管并保留 timeScale 的原始基准，恢复保存的 timeScale 时不会重复相乘。

配合 TurnClockMod 时，可识别的缩放时间计时器按原生倍率与 Unity 当前帧时间缩放的组合补偿，实时计时器只补偿原生倍率；无法识别时钟类型的计时器保留原生补偿方式。详见[回合时钟说明](../TurnClockMod/README.zh-CN.md)。

## 构建、启用与回退

```powershell
dotnet build mods/SpeedHackMod/SpeedHackMod.csproj -c Release -p:NuGetAudit=false
dotnet test tests/SpeedHackMod.Tests/SpeedHackMod.Tests.csproj -c Release -p:NuGetAudit=false
```

构建产物位于 `mods/SpeedHackMod/bin/Release/netstandard2.0/`。
退出游戏后将生成的 `SpeedHackMod.dll`、PDB 和清单替换到 mod 目录，保留自己的 `config.json`。
本版本应同时更新配套原生加载器与 SDK；旧文件通道不作为 UDP 的备用通道。Unity 模式的倒计时补偿需要搭配更新后的 TurnClockMod。模组及加载器配置无需替换。

将 `accelerationMode` 设为 `unity` 启用新模式；设为 `native` 返回原有机制，支持配置热重载。
原有倍率、按键和存档配置保持不变。可以直接回退到旧版 mod，无数据格式迁移。

## 验证

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

# Game/ — 反编译保留的游戏实现

`EventBridge.cs` 与 `EventListener.cs` 是从游戏客户端 `FairyGUI` 里反编译出来的原始实现，只在集成测试里编译使用（`AutoThanksMod.IntegrationTests`）。

放进来是因为集成测试要验证 **真实** 的事件派发/Retain/Release 行为：如果换成测试自己写的一份近似实现，`RealEventBridgeLocksDuringAnimationAndChatRequiresSuccess` 这类用例就失去意义了。`GameShims.cs` 只提供派发管道和 UI 类型的外壳，事件本身仍然走这两份实现。

- 不要为了"修测试"改这里的逻辑 —— 它们必须保持和游戏内一致；行为要改，改的是 `GameShims.cs` 或被测的产品代码。
- 游戏更新后如确认这两处实现有变化，按新版反编译结果整体替换。
- 这两个文件来自游戏，**不在本仓库的 AGPL-3.0 / MIT 授权范围内**，仅作为测试用的编译输入随仓库提供。

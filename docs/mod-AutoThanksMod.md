# AutoThanksMod：自动感谢与结算点赞

AutoThanksMod 1.1.0：队友治疗你、给你牌或转星币给你时，默认等待 1 秒后发送游戏原有的「快捷回复：感谢！」；游戏结束后，默认每隔 500ms 自动点赞其他真人玩家。两项功能默认开启，不需要手点，回放与观战不发送。感谢不受游戏「自动快捷回复提示」开关影响，与手点原版按钮一样向房间内玩家广播。

## 安装与配置

把 `AutoThanksMod.dll`、同次构建的 `AutoThanksMod.pdb` 与 `AutoThanksMod.json` 放到游戏目录的 `AstralParty_ModLoader/mods/AutoThanksMod/`，启动游戏。需要已安装的 CesiumLoader SDK 2.3.0 或更高版本。生命周期沿用 SDK 默认的 30 秒启动延迟。

首次初始化会创建同目录的 `config.json`；已有配置只补充缺失字段，保留原有设置：

```json
{
  "enabled": true,
  "thankForHealing": true,
  "thankForCards": true,
  "thankForTransfers": true,
  "cooldownSeconds": 2,
  "thankDelayMilliseconds": 1000,
  "autoLikeOnGameEnd": true,
  "likeIntervalMilliseconds": 500
}
```

- `enabled`：整个模组的总开关，控制自动感谢和结算自动点赞。
- `thankForHealing`：治疗后感谢。
- `thankForCards`：给牌后感谢。
- `thankForTransfers`：收到队友转星币后感谢。
- `thankDelayMilliseconds`：从收到帮助通知到发送感谢的等待时间，真实时间毫秒数，范围 0–3000，默认 1000。可配置 1500 等值；超过 3000 按 3000 执行，负数按 0 执行。
- `cooldownSeconds`：两次自动感谢的最小间隔，真实时间秒数，范围 0–30，默认 2。与触发延迟独立：到达发送时间后，如果仍在冷却内，该通知丢弃，不继续积压重试。
- `autoLikeOnGameEnd`：结算自动点赞的独立开关。开启时点赞本局其他真人，包括对手，跳过自己、机器人、怪物和已经手动点赞的玩家。
- `likeIntervalMilliseconds`：两次自动点赞之间的最小间隔，真实时间毫秒数，范围 100–30000，默认 500。第一人从四人结算页面准备好后开始；之后每次更新最多处理一人，卡顿后不集中补发。观察到手动点赞后也会等待这个间隔。

配置保存后约 1 秒内自动热重载（CesiumLoader 2.6.0 及以上），Toys 的模组配置表单会自动显示新增开关和数字字段。`enabled=false` 生效后还原自己的帮助通知回调，清空未发送的感谢并暂停点赞；`autoLikeOnGameEnd=false` 只暂停自动点赞。禁用再启用仍保留本局已尝试点赞的记录。延迟或间隔修改后，未处理事件使用当前配置值。

房间或账号切换、退出对局、观战、回放及卸载时清理会话。进入结算立即清空未发送的感谢，点赞仅在结算页面的玩家区域可操作期间执行；离开该页面后停止剩余操作。启动模组时若已经处于结算页，不补发点赞；必须先观察到本人参与的真实 RUNNING 对局。

## 实现依据

服务器 `1098 / SayPhraseNotifyS2C.Phrase` 自带 `ActivePlayerId`（帮助者）、`PassivePlayerId`（接受帮助者）、`TriggerType`。类型 `CureFriend=3` 对应短语 `50001`，`GiveCard=2` 对应 `50002`，`TransferGold=1` 对应 `50000`；国服当前 STRChat 配置中三者均为「感谢！」。仅接受发送者非本人、接受者为真实账号 ID、双方同队、发送者未被屏蔽的这三种事件。

不根据血量增长或手牌增长猜事件，不处理击杀 Boss。`AccountLogic.IsSelf` / `GetSelfPlayerData` 可能在观战中重定向，所以使用 `account.GetPlayerID()` 并显式过滤 `watch.PlayerIsWatcher()` 与 `replay.Session.IsReplay`。只在 Battle 场景、房间 RUNNING、客户端准备就绪时挂钩。

RPC 回调仅复制通知进有界队列，不读游戏单例、不调用 Unity 或发送消息。OnUpdate 在主线程等待配置的延迟，然后重新验证当前玩家、队伍、屏蔽、开关和冷却，超过 5 秒的通知丢弃。每局/回调重置后自动重新包装，保留原回调的调用和 UniTask 返回值；卸载只恢复仍位于回调顶层的自身包装器，不覆盖其他 mod 的回调。代际标识让旧包装器失效，避免重新包装时重复入队。

感谢发送使用 `communicate.RequestRoomShortChatC2S(chatId)`；获得非空请求结果后派发 `BattleMessage(MessageType.SHORTINFO, self, chatId)`，与原按钮保持本地显示语义。发送前检查 SDK `GameActions` 权限。短语无需 Action.Sn，也不取消行动倒计时。日志「已请求发送感谢」仅表示已调用发送路径，不宣称服务端已成功广播。

结算点赞只读反射 `BattleSettlementPanel._BattleSettlement`，按原版 `RefreshAchieveInfo` 的索引绑定获取实际玩家 ID。四人页准备好后调用原版 `btn_Praise.onClick.Call()`，保留按钮锁定、点赞动画、`battleResult.RequestPraisePlayerC2S(new List<long> { playerId })` 及成功回执后的 `BattlePraiseMessage` 聊天反馈。不会更改点赞 RPC 委托，也不按昵称、槽位控制器或图片推断玩家身份。

PVP 单胜者展示页使用独立按钮：同时观察该按钮的动画和灰态，把手动点赞记入同一玩家 ID 去重表，后续四人页不重复发送。四人页的 `isFinish=1` 或点赞动画进行中也视为已尝试；原版本地标志不代表服务器已成功。每局每人最多自动尝试一次，失败或抛异常不自动重试。页面刷新时保留去重记录并恢复已尝试按钮的锁定状态。发送前检查 SDK `GameActions` 权限。日志「已触发原版结算点赞」不代表服务器已接受。

感谢延迟、冷却、通知过期及点赞间隔均使用 `DateTime.UtcNow.Ticks`，不使用受变速引擎影响的 Time/QPC。

## 构建与验证

```powershell
dotnet build mods/AutoThanksMod/AutoThanksMod.csproj -c Release
dotnet test tests/AutoThanksMod.Tests/AutoThanksMod.Tests.csproj -c Release
dotnet test tests/AutoThanksMod.IntegrationTests/AutoThanksMod.IntegrationTests.csproj -c Release
dotnet test tests/AutoThanksMod.LifecycleTests/AutoThanksMod.LifecycleTests.csproj -c Release
```

构建、协议映射和元数据合约测试需要工作区的 `extracted_dlls/AstralParty.Runtime.dll` 等游戏程序集；结算事件集成测试另外直接编译 `decomp/_full/FairyGUI/EventBridge.cs` 和 `EventListener.cs`。这些游戏私有文件不随加载器仓库分发。生命周期测试使用最小单例、配置和 RPC 桩执行实际 `ModEntry`，不启动游戏。

策略测试覆盖实际协议枚举映射、玩家过滤、独立开关、延迟边界及 3 秒上限、延迟与冷却组合、过期通知、点赞间隔、跨局重置和每人去重。离线集成测试执行实际结算点赞代码，并使用游戏原有的 FairyGUI EventListener/EventBridge 验证按钮锁定与事件行为；生命周期测试执行实际 OnUpdate，覆盖真实对局到结算、回放/观战/退出过滤、配置禁用、延迟队列清理和卸载回调恢复。已加入 `tools/builtin-mods.json`，标准打包流程会构建和复制 DLL/PDB/元数据。实际服务器接受和 HybridCLR 中的执行仍需真机对局验证。

真机验证：分别让队友治疗一次、给牌一次、转星币一次，确认默认约 1 秒后出现感谢；把延迟配置为 3000 和超过 3000，确认最多等待约 3 秒；延迟期间退出对局或禁用功能不再发送。真实对局结束后，其他真人依次获得点赞，默认间隔至少 500ms；PVP 赢家页先手动点赞赢家，四人页不会重复。手动与自动交错、刷新结算页面、退出结算及回放/观战均检查无重复或额外发送。检查 activity-mod 日志中的挂钩、感谢发送和点赞触发记录。

源码与模组适用仓库 AGPL-3.0 许可证。

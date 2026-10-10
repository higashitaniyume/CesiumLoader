# AutoThanksMod：自动感谢

队友治疗你、给你牌或转星币给你时，自动发送游戏原有的「快捷回复：感谢！」。默认开启，不需要手点；不受游戏「自动快捷回复提示」开关影响。感谢与手点原版按钮一样向房间内玩家广播。

## 安装与配置

把 `AutoThanksMod.dll`、同次构建的 `AutoThanksMod.pdb` 与 `AutoThanksMod.json` 放到游戏目录的 `AstralParty_ModLoader/mods/AutoThanksMod/`，启动游戏。需要已安装的 CesiumLoader SDK 2.3.0 或更高版本。生命周期沿用 SDK 默认的 30 秒启动延迟。

首次初始化会创建同目录的 `config.json`：

```json
{
  "enabled": true,
  "thankForHealing": true,
  "thankForCards": true,
  "thankForTransfers": true,
  "cooldownSeconds": 2
}
```

- `enabled`：自动感谢总开关。
- `thankForHealing`：治疗后感谢。
- `thankForCards`：给牌后感谢。
- `thankForTransfers`：收到队友转星币后感谢（默认开启）。
- `cooldownSeconds`：两次自动感谢的最小间隔，真实时间秒数，范围 0–30。默认 2 秒；间隔内的通知合并为之前那次感谢，不排队延后刷屏。

配置每两秒重新读取。设 `enabled=false` 后会还原自己安装的回调并清空待发送通知。房间切换、观战、回放、结算及卸载时同样清理。

## 实现依据

服务器 `1098 / SayPhraseNotifyS2C.Phrase` 自带 `ActivePlayerId`（帮助者）、`PassivePlayerId`（接受帮助者）、`TriggerType`。类型 `CureFriend=3` 对应短语 `50001`，`GiveCard=2` 对应 `50002`，`TransferGold=1` 对应 `50000`；国服当前 STRChat 配置中三者均为「感谢！」。仅接受发送者非本人、接受者为真实账号 ID、双方同队、发送者未被屏蔽的这三种事件。

不根据血量增长或手牌增长猜事件，不处理击杀 Boss。`AccountLogic.IsSelf` / `GetSelfPlayerData` 可能在观战中重定向，所以使用 `account.GetPlayerID()` 并显式过滤 `watch.PlayerIsWatcher()` 与 `replay.Session.IsReplay`。只在 Battle 场景、房间 RUNNING、客户端准备就绪时挂钩。

RPC 回调仅复制通知进有界队列，不读游戏单例、不调用 Unity 或发送消息。OnUpdate 在主线程逐条验证，超过 5 秒的通知丢弃。每局/回调重置后自动重新包装，保留原回调的调用和 UniTask 返回值；卸载只恢复仍位于回调顶层的自身包装器，不覆盖其他 mod 的回调。代际标识让旧包装器失效，避免重新包装时重复入队。

发送使用 `communicate.RequestRoomShortChatC2S(chatId)`；获得非空请求结果后派发 `BattleMessage(MessageType.SHORTINFO, self, chatId)`，与原按钮保持本地显示语义。发送前检查 SDK `GameActions` 权限。短语无需 Action.Sn，也不取消行动倒计时。日志「已请求发送感谢」仅表示已调用发送路径，不宣称服务端已成功广播。

冷却与通知过期使用 `DateTime.UtcNow.Ticks`，不使用受变速引擎影响的 Time/QPC。发送失败也保留冷却，不自动重试。

## 构建与验证

```powershell
dotnet build mods/AutoThanksMod/AutoThanksMod.csproj -c Release
dotnet test tests/AutoThanksMod.Tests/AutoThanksMod.Tests.csproj -c Release
```

离线测试覆盖实际协议枚举映射、非本人/自己/非队友/屏蔽过滤、独立开关、跨类型冷却、过期通知与跨局重置。已加入 `tools/builtin-mods.json`，标准打包流程会构建和复制 DLL/PDB/元数据。实际服务器广播和 HybridCLR 中的执行仍需真机对局验证。

真机验证：在普通对局分别让队友治疗一次、给牌一次、转星币一次，确认无需点按钮便出现感谢；连续帮助在默认 2 秒内只自动发一次；回放和观战不自动发。检查 activity-mod 日志中的挂钩和发送记录。原版快捷回复提示保持游戏自己的行为。

源码与模组适用仓库 AGPL-3.0 许可证。

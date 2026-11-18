# SDK：预测动作前置处理（实验性）

> `PredictActionPreDispatch` 是针对 **`PredictActionS2C` 动作分发链**的可选前置事件，不是 Harmony / Mixin，也不能拦截任意 C# 或 HybridCLR 方法。使用本接口的 Mod 必须在完成游戏协议应答后，才可以声明已处理动作。

## 适用场景

服务器向客户端发送需要玩家回应的预测动作时，原版游戏通常会展示对应的交互窗口。普通事件监听只能观察到这次动作；即使 Mod 已经替玩家回应，原版回调仍可能继续创建窗口。

前置事件使 Mod 能够在原版动作进入 `ActionListener` 之前判断并回应，例如：

- 玩家余额不足时，自动拒绝当前商店报价（**下文提供完整示例**）；
- 在有明确规则的情况下，自动确认某种可跳过的交互；
- 自动应答其他需要玩家选择、且具备明确原版协议回复的动作。

SDK **不会**替 Mod 猜测选择，也不会自动发送请求、解释价格或修改服务器消息。这些业务判断始终留给 Mod。

## 调用流程和返回值

1. `GameEvents` 在收到有效的 `PredictActionS2C` 后，检查是否存在前置事件订阅者；没有时直接走原版回调。
2. 有订阅者时，将符合条件的动作调度到 Unity 主线程。每个动作包含 `ActionId`、`PlayerId`、`Sn`、`Data`。
3. 订阅者对动作进行检查：不认识、不满足条件或未完成协议应答时返回 `false`。
4. 订阅者已**成功发起该动作所需的完整原版协议应答**时，才返回 `true`。SDK 记录该动作的 `Sn` 已完成。
5. 原始 RPC 消息仍完整转发给原版游戏。游戏的 `ActionListener` 会检查已完成的 `Sn`，据此跳过对应动作的后续处理；同一批次中的其他动作不受影响。

| 返回值 | 意义 | 注意事项 |
|---|---|---|
| `false` | 当前 Mod 没有完成处理，保留原版流程 | 观察用途、免费商品、异常报价都应返回 `false` |
| `true` | Mod 已通过游戏原版协议发起有效回应 | **不能**只因为准备处理、安排异步任务或单纯想隐藏窗口就返回 `true` |

> 这里的“成功发起”不等于“服务器已确认”。网络异常或服务端拒绝仍可能导致状态不同步。正式多人环境使用前需要完整测试。

## 完整示例：筹码商店买不起时自动拒绝

以下为**独立 Mod 侧示例代码**，不应放入 CesiumLoader SDK。示例沿用游戏热更新代码中的筹码商店请求，展示如何使用通用前置事件。其他 Mod 可以替换 `ActionId`、消息类型及原版请求方法，实现自己的逻辑。

```csharp
using System;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using Core.Net;
using GameLogic;
using party.protocol;

internal static class RelicOfferExample
{
    // 游戏原版动作编号：收到筹码商店报价。
    private const int BuyRelicActionId = 5249;
    private static bool _subscribed;

    public static void Start()
    {
        if (_subscribed) return;
        PredictActionPreDispatch.BeforeDispatch += OnBeforeDispatch;
        GameEvents.StartAutoHook();
        _subscribed = true;
    }

    public static void Stop()
    {
        if (!_subscribed) return;
        PredictActionPreDispatch.BeforeDispatch -= OnBeforeDispatch;
        _subscribed = false;
    }

    private static bool OnBeforeDispatch(PredictActionRequest request)
    {
        if (request == null || request.ActionId != BuyRelicActionId)
            return false;

        try
        {
            if (request.Sn <= 0 || !GameLogicManager.hasInstance)
                return false;

            GameLogicManager game = GameLogicManager.inst;
            BattlePlayerData self = game.battle?.GetSelfPlayerData();
            if (self == null || game.land == null || self.player.Id != request.PlayerId)
                return false;

            byte[] data = request.Data; // 返回数据副本，不会修改服务器消息。
            if (data.Length == 0) return false;
            BuyRelicC2S offer = ByteBuf.ReadObject<BuyRelicC2S>(data);
            int price = offer.RelicGold;
            int gold = self.Property.gold.Value;

            // 0 星币可以免费购买；未知或负数价格也不应自动取消。
            if (gold < 0 || price <= 0 || gold >= price)
                return false;

            // 调用游戏原版的“拒绝购买”请求。
            // 原版方法会取消操作计时，并记录完成序号 Sn。
            bool sent = game.land.RequestBuyRelicC2S(request.Sn, 0) != null;
            if (sent)
                SdkLog.Info("RelicOfferExample", "余额不足，已尝试拒绝筹码商店报价：sn=" + request.Sn);
            return sent;
        }
        catch (Exception e)
        {
            SdkLog.Warn("RelicOfferExample", "处理失败，交还原版：" + e.Message);
            return false;
        }
    }
}
```

接入 Mod 的生命周期（伪代码，具体重写位置以所用 ModBase 版本为准）：

```csharp
public override void OnInitialize() => RelicOfferExample.Start();
public override void OnUnload() => RelicOfferExample.Stop();
```

本示例**不会**修改商品报价、金币或原版窗口内容。余额充足、商品免费或者数据异常时会正常打开原版界面。多个 Mod 订阅时，第一个返回 `true` 的订阅者终止该动作的后续订阅者处理。

## 能解决什么，不能解决什么

- **可以：**针对 `PredictActionS2C` 中的特定动作，在原版处理前完成协议回应，从而避免部分等待用户操作的窗口出现。
- **不能：**无条件跳过任意游戏方法、改变服务端规则、忽略移动结算或消除所有地块动画。

## 安全边界与回退

1. 无订阅者：直接执行原版回调，不额外执行前置业务逻辑。
2. 订阅者返回 `false` 或抛异常：交给后续订阅者，最终由原版处理。
3. 只允许有效 `Sn`、需要广播并且属于本地玩家的动作进入可处理流程；其他玩家的动作可使用现有观察事件。
4. 事件在主线程执行；调度器不可用时不拦截。**不要**在回调中等待网络结果或同步阻塞线程。
5. `Data` 是复制的数据，订阅者不能通过修改它来修改原版服务器消息。
6. 原版请求方法可能已记录 `Sn`，SDK 的完成记录需关注重复记录是否影响队列；如果错误地返回 `true`，可能造成操作卡住。
7. 切换场景、断线重连、网络拒绝与混合动作包需要单独验证。测试前不建议在正式多人对局中使用主动拦截。
8. Mod 卸载时必须取消订阅，避免热加载后重复处理。

## 构建与测试

SDK 单独构建（不需要启动 Visual Studio IDE，可以用 Rider 终端）：

```powershell
dotnet build loader/CesiumLoader.SDK/CesiumLoader.SDK.csproj -c Release
dotnet test tests/CesiumLoader.SDK.Tests/CesiumLoader.SDK.Tests.csproj -c Release
```

完整加载器构建需要 Windows、VS2026 对应 C++ 工具链和 Windows SDK。建议按顺序验证：无订阅、只返回 `false`、成功回应后返回 `true`、免费商品、其他玩家、重复 `Sn`、异常订阅者、断线与重新进入对局。只有构建与游戏内测试都通过，才能把实验接口作为稳定功能使用。

# SDK：上行 RPC 拦截（实验性）

> `RpcGate` 能把某个 **C2S 上行请求**拦在本地、不发往服务器，同时给调用方一个"本地已完成"的返回值，让游戏自己的后续逻辑照常跑。
>
> 它**不是**通用方法 Hook，也**不能**拦截任意方法。可用范围只有一类：走*生成式 RPC 类*发出的 C2S 请求。

## 适用场景

游戏里每个 C2S 请求都对应一个生成类，形状完全一致：

```csharp
// Core.Net/RPCMsgManager.cs  —— 容器上是 public 实例字段
public ChooseSkinC2SRPC ChooseSkinC2S = new ChooseSkinC2SRPC();

// Core.Net/ChooseSkinC2SRPC.cs —— 类 public、方法 public virtual、发送只有一行
public class ChooseSkinC2SRPC
{
    public virtual RPCAsyncResult ChooseSkinC2SCall(ChooseSkinC2S req)
        => RPCMsgManager.RPCCallStatic(req, 5305, ProtolcalType.Ahead);
}
```

调用方总是这个形状：`MonoSingletonProvider<NetManager>.inst.RPC.<XxxC2S>.<XxxC2SCall>(req)`。因为调用点是对该字段的**虚调用**，**用一个子类覆盖它、再把字段换掉**就能拦下这次请求 —— 纯托管、一次性、可还原，不需要原生 Hook，也不需要改 IL。

典型用途：

- 某个操作在本地不被允许（缺少前置条件、玩家关掉了开关），但游戏的原版 UI 流程必须照常走完；
- 只在特定参数下抑制请求，其它情况完全放行；
- 需要"看起来成功了"但不想让服务器看到这次操作。

## 边界：能解决什么，不能解决什么

| 能 | 不能 |
|---|---|
| 拦下走生成式 RPC 类的 C2S 请求 | 拦下直接调 `RPCMsgManager.RPCCallStatic` 的代码 |
| 让调用方拿到一个本地完成的 `RPCAsyncResult` | 改变服务器状态（**服务器收不到这个请求**） |
| 按请求内容决定放行/拦截 | 修改请求内容后照常发送（本 API 不这么做） |
| Dispose 后完全还原 | 拦截 S2C（那是 `GameEvents` 的领域，用委托槽位） |

**拦截只影响本地**：服务器收不到这次请求，因此服务器状态不会改变，其它玩家也看不到你在本地做成的样子。被拦下的关键流程 RPC（移动/出牌等）只会让自己的客户端与服务端不一致，**不会骗过服务器**。

## 用法

每个 RPC 只需要写一个壳（约 3 行）加一次安装。下游任何位置调用这个 RPC 都会被拦到 —— 包括游戏自己的 UI 代码，因为换掉的是**游戏实际持有的那个字段**：

```csharp
using System;
using Core.Net;
using CesiumLoader.SDK.Networking;

internal sealed class SkinGateRpc : ChooseSkinC2SRPC
{
    internal Func<ChooseSkinC2S, bool> Block;

    public override RPCAsyncResult ChooseSkinC2SCall(ChooseSkinC2S req)
        => RpcGate.Intercept(req, Block, "SkinGate")     // 拦下 → 返回本地完成的结果
              ?? base.ChooseSkinC2SCall(req);            // 放行 → 走原实现，逐字节一致
}
```

```csharp
private IDisposable _sub;

void Enable()
{
    // 泛型实参必须显式写「字段的声明类型」（游戏生成类的基类）
    _sub = RpcGate.Install<ChooseSkinC2SRPC>(new SkinGateRpc { Block = ShouldBlock }, "SkinGate");
}

// 只在"选中未拥有的皮肤"这条路径上拦；确认路径（Affirmed=true）必须放行
static bool ShouldBlock(ChooseSkinC2S req) => !req.Affirmed && Targets(req.UseAdorn);

void Disable() { _sub?.Dispose(); _sub = null; }
```

> **泛型实参**：`Install<T>` 的 `T` 是**字段的声明类型**，不是你的子类类型。写成 `RpcGate.Install(new SkinGateRpc())` 会推断成子类、找不到字段（SDK 记一条日志后放行）。必须像上面那样显式写 `<ChooseSkinC2SRPC>`。

> **最好用 `new` 建壳、把 `Block` 设好再装**：`Install` 会立刻尝试替换字段并开始工作，这时壳上的 `Block` 若还是 null，`Intercept` 一律放行（安全，但那一段时间的请求不会被拦）。

## 为什么拦截时必须返回"稍后完成"的结果

C2S 的调用点普遍写成 `RequestXxx(...).OnFinishedOnly.AddOnce(...)` —— **先拿到返回值、再把回调挂上去**。而 `Tools.Signal` 没有"已派发后补挂立刻回调"的重放语义（`AddOnce` 只是把委托加进列表，`Dispatch` 只调用当下在册的监听者）。

所以结果必须在**调用方挂完回调之后**才完成：

| 返回值 | 后果 |
|---|---|
| 返回前就标成已完成 | `AddOnce` 永不触发 → 按钮/等待链**永久卡死** |
| 返回一个永不完结的结果 | 同样卡死 |
| **返回未完成、下一帧完成**（本 SDK 的做法） | 回调正常触发，`keepWaiting` 语义也成立 |

`RpcGate.Intercept` / `RpcGate.CompletedLater` 的做法是"登记一个动作，下一帧在主线程完成"；另有一个 1 秒的兜底节拍，万一某次下一帧没跑起来（帧循环停摆），被拦下的调用仍会在有限时间内完成，不会把调用方的等待链挂住。

## 失败一律放行

任何一步不成立都**放行原版请求**并记日志 —— 拦截失败退化成"和原版一样"，绝不退化成"卡死"：

| 情况 | 行为 |
|---|---|
| 找不到字段（游戏更新改名/删除） | 记 WARN，不拦截 |
| 同类型有多个字段（歧义） | 记 WARN，不拦截（拦错字段比起不拦危险得多） |
| 判定委托为 null / 请求为 null | 不拦截 |
| 判定委托抛异常 | 记一次日志，不拦截 |
| 拿不到 `RPCAsyncResult` 的构造或 `Finish()` | 记 WARN，不拦截 |
| 单例尚未就绪 | 跳过，等下一个周期重试 |

## 生命周期

`NetManager.Clear()` 会执行 `RPC = new RPCMsgManager()`，**重建整个容器**，换过的字段随之丢失。SDK 因此每秒检查一次并自动重挂（与既有的 RPC 回调重挂节奏一致），无需 mod 干预。

**mod 卸载时必须 `Dispose`**，否则热加载后会留下悬挂实例。`Dispose` 只在字段里还是我们的实例时才写回原实例 —— 别人（或游戏自己）已经改过就不动，不覆盖非我们造成的状态。

## 线程与性能

- `Intercept` 在**游戏调用栈**里同步执行（主线程，和原版发送路径一致）。判定委托必须**快速返回**，不要等待网络、不要加锁等待。
- 放行路径零额外开销（`base` 调用，与原版逐字节一致）；拦截路径有一次反射构造 + 一次延迟完成。
- 安装/重挂每秒一次，开销可忽略。

## 已知限制

1. 只覆盖生成式 RPC 类的 C2S 入口；直接调 `RPCMsgManager.RPCCallStatic` 的内部代码不受影响。
2. 判定委托抛异常时按"放行"处理 —— 不要在判定里做有副作用的操作后依赖它被拦下。
3. 拦截后服务器看不到该请求：本地展示与服务器状态会不一致，**只用于本地表现**，不要用于需要服务器确认的流程。
4. `Install` 每个 RPC 类型的首次安装会反射查字段；游戏更新改了类名/字段名时会在日志里明确报出来，此时行为回到原版。

## 构建与测试

```powershell
dotnet build loader/CesiumLoader.SDK/CesiumLoader.SDK.csproj -c Release
dotnet test tests/CesiumLoader.SDK.Tests/CesiumLoader.SDK.Tests.csproj -c Release
```

单元测试只覆盖 `RpcGate` 的纯逻辑内核（字段发现、判定护栏、延迟完成队列、换/还原语义），这些逻辑不引用游戏类型。字段替换与延迟完成依赖游戏类型，必须在游戏内验证：无订阅、放行、拦截、判定抛异常、容器重建后重挂、Dispose 还原。

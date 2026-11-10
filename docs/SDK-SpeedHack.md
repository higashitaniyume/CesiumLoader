# SDK-SpeedHack（变速）

`CesiumLoader.SDK.Speed.SpeedHack` 控制原生时钟倍率。内置 SpeedHackMod 另提供 `native` 与 `unity` 两种加速方式，见[模组说明](../mods/SpeedHackMod/README.zh-CN.md)。

## 范围与时间语义

- 原生倍率范围为 **1–100**；低于 1、NaN 和无穷大请求被拒绝，不改变引擎状态。
- `1x` 表示恢复正常速度，不卸载时间 hook。切换时仍使用连续虚拟时间公式，避免时钟倒退。
- 原生变速影响动画、演出、回合计时与网络超时，建议不超过 `3x`。客户端倒计时可配合 TurnClockMod，但不能延长服务器操作期限。
- 原生层 hook `GetTickCount`、`GetTickCount64`、`timeGetTime` 和 `QueryPerformanceCounter`。Unity 模式由模组把原生恢复到 1x，再调整游戏时间。

## 本地 UDP 通道

加载器为每个游戏进程绑定独立的 `127.0.0.1` 临时 UDP 端口，通过 **`CESIUM_SPEED_PORT`** 进程环境变量发布。多个账号即使共用模组目录，请求和回执仍按进程隔离。

SDK 使用托管 Socket，不依赖热更程序集无法使用的原生 P/Invoke。请求、查询与回执全部通过 UDP；**不再读写 `request.txt`、`state.txt` 或 LocalAppData 镜像**，也不会回退到残留文件。需要配套支持此通道的加载器和 SDK。

- `speedControlEnabled=false` 时不提供该通道。
- 端口未提供、服务暂不可达或回执过期时，状态为未知，可继续重试。
- SDK 收发为非阻塞操作，后台处理回执；活跃期间查询／重试间隔至少 50ms，无调用一段时间后暂停常规查询。
- 状态有效期为 0.5 秒；设置倍率会立即使旧确认失效。请求有递增编号、回执关联及状态修订号，拒绝过期或乱序回执。
- 设置请求在 2 秒内按同一编号重试。原生侧去重，避免重试覆盖后续的外部倍率变更。

### 协议

请求为无尾部换行的 ASCII 数据报，最长 511 字节：

```text
get <id>
set <operation-id> <speed> <attempt-id>
```

编号为客户端 Socket 内递增的正 64 位有符号整数。重试保持 `operation-id` 不变，每次使用新的 `attempt-id` 关联回执，防止旧回包借用新请求的有效期。原生侧也接受省略 `attempt-id` 的简单设置请求，此时以 `operation-id` 关联回执。`speed` 为点号十进制，不接受指数、逗号小数点或低于 1 的值。SDK 使用固定三位小数。

回执为 ASCII `key=value` 行：

```text
protocol=1
id=123
revision=2
ok=1
version=3.5.2
speed=2.000
base=2.000
active=1
hooks=4
```

`id` 对应请求，`revision` 在倍率成功设置后递增，`ok` 表示请求是否被接受。`speed` 始终是实际倍率，`base` 是启动配置中的基础倍率。失效的引擎、重复字段和非法数值不能作为有效确认。

## API

```csharp
using CesiumLoader.SDK.Speed;

bool available = SpeedHack.IsAvailable; // 初次查询可能尚未收到回执，稍后重试
bool sent = SpeedHack.SetSpeed(2.0);    // 仅表示请求已发送，不表示实际生效

if (SpeedHack.TryGetSpeed(out double actual))
{
    // 此处 actual 是有效回执中的实际倍率。
}
// false 表示未知，不能据此认定已恢复 1x。

SpeedHack.Reset();                     // 异步请求 1x，不卸载 hook
double basis = SpeedHack.BaseSpeed;    // 未知时返回 1x
string diagnostics = SpeedHack.Diagnostics();
SpeedHack.Refresh();                   // 清除缓存、关闭旧客户端端点，下次重新发现
```

`Speed` 为兼容 API，未知时返回 `1x`。涉及模式切换、物理步长或计时补偿时，应使用 **`TryGetSpeed`**，不要把默认值当作确认。`SetSpeed` 返回 false 表示倍率非法、端口不可用或发送失败；返回 true 后应继续读取实际状态。

`Directory` 仅保留为旧代码的路径元数据兼容 API，不再承载通信。

## 热键辅助

```csharp
double next = SpeedHack.StepSpeed(2.0, +1, step: 0.5, min: 1, max: 10);
double safe = SpeedHack.ClampSpeed(next, min: 1, max: 10);
```

这些方法只做计算。无效参数使用安全默认值，下限始终不低于 1；结果规整至三位小数。常量为 `MinSpeed=1`、`MaxSpeed=3`、`HardMaxSpeed=100`、`DefaultSpeedStep=0.5`。

模组配置仍保存在 `mods\{ModId}\config.json`。UDP 仅替换通信机制，不改变配置保存位置、按键或倍率范围。加载器基础倍率由 `doorstop_config.json` 中的 `speedhackBaseSpeed` 控制。

原生调用方仍可使用 `ap_speed_set`、`ap_speed_get` 和 `ap_speed_active` 导出；热更模组请使用 SDK。

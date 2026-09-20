# mod：FreeCameraMod（俯瞰视角）

> 源码：`src/FreeCameraMod/ModEntry.cs`。纯 SDK 用户，不使用任何反射或内部 API。

## 1. 它解决什么

本作是棋盘类游戏，默认相机机位很低、需要拖动鼠标才能看全地图。这个 mod 提供**固定俯瞰视角**：
直接把主相机抬到棋盘点上方俯视，一眼看完整个棋盘。

## 2. 配置

配置文件：`AstralParty_ModLoader\mods\FreeCameraMod\config.json`

```json
{
  "mode": "preset",
  "height": 160,
  "pitch": 70,
  "fov": 80,
  "boardHeight": 0,
  "aimDistance": 0,
  "maxTargetDistance": 500,
  "autoActivate": true,
  "autoActivateDelay": 15,
  "orbitDistance": 12,
  "distanceStep": 1.5,
  "speed": 20,
  "rotationSpeed": 2,
  "nearClip": 0.05,
  "farClip": 2000,
  "invertY": false,
  "lockCursor": false,
  "toggleKey": "F1",
  "resetKey": "F2"
}
```

> ⚠️ 配置在 mod 初始化时**读一次**，改完需要**重启游戏**。
> ⚠️ 这个 JSON 解析器**不支持注释**（`//`）。写了注释会导致整份文件解析失败并被备份成
> `config.json.bak`，配置全部退回默认值。

### 关键字段

| 字段 | 说明 |
| --- | --- |
| `mode` | `preset`（固定俯瞰，默认）/ `orbit`（绕点旋转）/ `fly`（自由飞行） |
| `height` | **相机绝对高度**（世界单位）。越大看得越广。160 大约能覆盖整张棋盘 |
| `pitch` | 俯视角度。70° 接近垂直俯视；45° 更斜、更有立体感 |
| `fov` | 视野角。80 比游戏默认的 45 更广，配合高度能装下整张地图 |
| `boardHeight` | 棋盘所在平面的 Y 高度，用于计算"相机该往后退多少" |
| `aimDistance` | 观察点相对相机正前方的偏移。>0 时观察点更远，视角更平 |
| `maxTargetDistance` | 自动找棋盘中心时允许的最大搜索半径 |
| `autoActivate` | 启动后自动进入俯瞰视角（默认 true） |
| `autoActivateDelay` | 进入前的等待秒数（默认 15）。菜单/加载阶段的相机是占位相机，太早接管会闪 |
| `nearClip` / `farClip` | 裁剪面。`farClip` 必须够大（2000），否则远处棋盘会被裁掉 |
| `toggleKey` | 开关热键，**默认也只有 F1** |
| `resetKey` | 恢复默认视角（F2） |

## 3. 热键

**只绑定 F1（开关）和 F2（复位）**，没有其它热键，也不做任何游戏内参数调节。
这是刻意的设计：这个游戏本身用鼠标拖动视角，抢走鼠标会严重影响正常游玩。

## 4. 自动进入逻辑

1. 启动后每 30 帧检查一次；
2. 未到 `autoActivateDelay` 秒 → 继续等；
3. 当前相机是**占位相机**（位置 (0,0,0)、旋转 (0,0,0)）→ 继续等，并记录
   `自动进入推迟: 主相机尚未就绪(占位相机)`；
4. 相机就绪 → 记录 `自动进入俯瞰视角`，保存 `CameraState` 快照后接管。

## 5. 接管方式与可观测性

- **接管**：每帧在 `PlayerLoopTiming.PostLateUpdate` 写入相机位姿/镜头参数。
  选这个时机是因为它排在游戏的 `LateUpdate` / Cinemachine 之后，写入不会被覆盖。
- **不接管时**：完全不碰相机，游戏一切如常。
- **退出时**：把进入前保存的 `CameraState` 原样写回。

日志里能看到三种校验（排查"看起来没生效"时先看这个）：

| 日志 | 含义 |
| --- | --- |
| `接管校验(第60帧): 实际 pos=… 期望 … 位置偏差=0m` | 写回后读回来一致 → 确实生效 |
| `写入生效` / `预设位姿没有写进相机` | 写入-回读对比 |
| `上一帧渲染用的就是预设视角(生效)` / `游戏在我们的写入之后又改了相机(未生效)` | 下一帧开头读取上一帧结果 |

## 6. 俯瞰几何

预设模式的相机位置由高度和俯角推出（见 `ComputePresetPose`）：

```
back   = (height - boardHeight) / tan(pitch)     // 水平后退距离
camera = target - horizontalForward(yaw) * back  // 相机世界坐标
rotation = Euler(pitch, yaw, 0)
```

`height` 是**绝对高度**（不是相对棋盘的偏移），所以调高度时不用担心棋盘在哪一层。

## 7. orbit / fly 模式（保留但默认不用）

- `orbit`：绕观察点旋转。**鼠标转向在本作里不可靠**——游戏自己每帧重写相机输入状态，
  SDK 的鼠标轴读取拿不到有效增量。需要严格可控的自动环绕时可用键盘/代码驱动。
- `fly`：无重力自由飞行，主要用于调试。

两者都保留实现（可继续维护），但推荐用 `preset`。

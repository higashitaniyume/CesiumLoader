# SDK 音频子系统

> 相关源码：`loader/CesiumLoader.SDK/Audio/`（`AudioService.cs`、`AudioEvents.cs`、`IAudioBackend.cs`、`UnityAudioBackend.cs`、`WavUtility.cs` 等）。
> 示例 Mod 见 `mods/AudioSampleMod/`。

## 1. 架构与分层

《Astral Party》原生采用 Audiokinetic Wwise 音频中间件，UI 与游戏逻辑层音效（含语音、按键、特效等）统一汇聚于 `FairyGUI.Stage.inst.playSoundCallback` 派发。
SDK 音频子系统通过拦截路由、挂钩统一事件出口，并在游戏内挂载原生 Unity Audio 源（`AudioSource`、`AudioClip`、`AudioListener`），提供高保真、低延迟的自定义音频播放与游戏音效拦截替换能力。

```
Mod 代码
  └─ AudioService             ← 统一音频门面：播放、通道音量、BGM 覆盖、规则注册
       ├─ IAudioBackend       ← 底层音频后端接口（解耦可替换）
       │    └─ UnityAudioBackend   ← 原生 Unity Audio 实现，维护 [CesiumAudioHost] 与各通道 AudioSource
       ├─ WavUtility          ← 纯托管 WAV PCM 编解码器（支持 8/16/24/32-bit 解码与 16-bit 编码）
       ├─ GameAudioBridge     ← 与游戏运行时（FairyGUI、Wwise RTPC、GameSettings）的解耦桥接
       └─ AudioEvents         ← 游戏原生音效事件监听与拦截中枢
```

在测试或离线环境中可通过 `AudioService.SetBackend(new FakeAudioBackend())` 自由切换测试后端，完全与 Unity 运行时解耦。

---

## 2. 音频播放与通道管理

SDK 定义了 5 种独立音频通道（`AudioChannel`）：`Master`、`Bgm`、`Sfx`、`Voice`、`Custom`。

### 2.1 播放一次性音效 (SFX)

```csharp
// 直接播放加载好的音频片段
AudioClipHandle clip = AudioService.LoadWav("MyMod/sfx.wav");
AudioService.PlayOneShot(clip, volume: 1.0f);

// 路径缓存播放（自动复用已加载缓存）
AudioService.PlayOneShot("MyMod/sfx.wav", volume: 0.8f);

// 3D 空间点播放
AudioService.PlayClipAtPoint(clip, new Vector3(0, 1, 0), volume: 1.0f);
```

### 2.2 背景音乐 (BGM) 覆盖与恢复

通过覆盖 BGM，可以在播放自定义背景音乐的同时静音游戏 Wwise 原生背景音乐；清除覆盖后自动平滑恢复原版音量：

```csharp
// 覆盖游戏原生 BGM
AudioClipHandle bgmClip = AudioService.LoadWav("MyMod/custom_bgm.wav");
AudioService.SetBgmOverride(bgmClip, volume: 0.7f, loop: true);

// 暂停与恢复自定义 BGM
AudioService.PauseBgm();
AudioService.ResumeBgm();

// 停止并恢复游戏原有 BGM
AudioService.ClearBgmOverride();
```

### 2.3 角色语音 (Voice) 播放

```csharp
AudioClipHandle voice = AudioService.LoadWav("MyMod/voice.wav");
AudioService.PlayVoice(voice, volume: 1.0f);

// 停止当前语音通道播放
AudioService.StopVoice();
```

### 2.4 通道音量与游戏设置联动

```csharp
// 设置指定通道音量
AudioService.SetChannelVolume(AudioChannel.Sfx, 0.8f);
AudioService.SetChannelVolume(AudioChannel.Bgm, 0.6f);

// 获取有效音量（已乘 Master 通道音量）
float effectiveSfxVolume = AudioService.GetEffectiveVolume(AudioChannel.Sfx);

// 主动从游戏系统设置同步音量（进入游戏或修改设置时）
AudioService.SyncVolumeFromGameSettings();
```

---

## 3. 游戏原生音效监听与拦截替换

游戏原生触发音效时，均会进入 `AudioEvents` 进行派发。Mod 可以通过事件委托或声明式规则进行静音、重定向或自定义音效替换。

### 3.1 声明式替换规则 (推荐)

使用 `AudioReplacementRule` 可以简洁地完成音效替换或静音：

```csharp
// 1. 将指定事件配置 ID 的音效替换为自定义音频片段
AudioClipHandle customClick = AudioService.LoadWav("MyMod/click.wav");
AudioService.RegisterReplacement(AudioReplacementRule.ForConfigId(1001, customClip: customClick));

// 2. 将指定 Wwise 事件名称的音效静音
AudioService.RegisterReplacement(AudioReplacementRule.MuteByName("ui_common_click"));

// 3. 将某个音效重定向为游戏内部的另一个音效 ID
AudioService.RegisterReplacement(AudioReplacementRule.Redirect(1001, redirectConfigId: 1002));

// 4. 使用谓词自定义匹配规则
AudioService.RegisterReplacement(new AudioReplacementRule
{
    Name = "MuteSkillVoiceRule",
    Predicate = e => e.EventName?.Contains("skill") == true,
    Cancel = true
});

// 清除所有注册的规则
AudioService.ClearReplacements();
```

### 3.2 动态挂钩事件 (进阶)

```csharp
// 挂钩声音播放前事件
AudioEvents.BeforeSoundPlay += OnBeforeSoundPlay;

// 挂钩声音播放后事件（用于统计或监控）
AudioEvents.AfterSoundPlay += (sender, args) =>
{
    CesiumLogger.Debug($"[Audio] Played sound: id={args.EventConfigId}, name={args.EventName}");
};

private static void OnBeforeSoundPlay(object sender, SoundPlayEventArgs e)
{
    // 按名称拦截 UI 点击音并替换
    if (e.EventName == "ui_common_click")
    {
        e.CustomClip = myCustomClip;
        e.CustomVolume = 0.8f;
        e.Cancel = true; // 阻止 Wwise 原生音效播放
    }
}
```

---

## 4. WAV 音频解码与合成

SDK 内置轻量、零依赖的纯托管 WAV 解码器：

```csharp
// 从本地文件加载（支持 8-bit、16-bit、24-bit PCM 及 32-bit IEEE 浮点格式）
AudioClipHandle clip = AudioService.LoadWav("path/to/sound.wav");

// 从字节数组解码
byte[] wavBytes = File.ReadAllBytes("path/to/sound.wav");
AudioClipHandle clipFromBytes = AudioService.LoadWav(wavBytes, name: "custom_sound");

// 生成程序合成正弦音（可用于调试提示音或占位音效）
AudioClipHandle beep = WavUtility.CreateSyntheticTone(
    frequencyHz: 880f,
    durationSeconds: 0.15f,
    sampleRate: 44100,
    volume: 0.5f,
    name: "SyntheticBeep"
);
```

---

## 5. 生命周期与清理规范

在 Mod 的 `OnUnload` 生命周期方法中，务必清理已注册的事件挂钩与自定义规则：

```csharp
public override void OnUnload()
{
    // 清除注册的替换规则与 BGM 覆盖
    AudioService.ClearReplacements();
    AudioService.ClearBgmOverride();
    
    // 取消订阅事件
    AudioEvents.BeforeSoundPlay -= OnBeforeSoundPlay;
}
```

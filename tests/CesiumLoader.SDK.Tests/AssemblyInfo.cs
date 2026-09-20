using Xunit;

// SDK 内部有大量进程级静态状态(ModRegistry / UpdateService / UiService / CameraService ...),
// 与真实游戏进程一致: 同一时刻只有一份。测试必须串行执行, 否则会互相干扰。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

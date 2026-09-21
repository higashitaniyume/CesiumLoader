using CesiumLoader.SDK;
using UnityEngine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 后端键位映射(Win32 回退路径)。ToVirtualKey 是纯静态函数, 不需要真的碰 P/Invoke。
    ///
    /// 这里主要盯"鼠标侧键"这类容易漏掉的映射: 漏一个就会退到 default: return 0,
    /// 表现为"用户把热键设成鼠标侧键后完全没反应", 而且没有任何报错 —— 只能靠断言兜住。
    ///
    /// ⚠️ 注意: 本文件刻意<b>不用</b> [Theory]/[InlineData] 把 KeyCode 当参数 ——
    /// xUnit 为了序列化用例数据会去反射枚举类型, 进而要加载 UnityEngine.SharedInternalsModule.dll,
    /// 而 extracted_dlls 里只有 CoreModule/AssetBundleModule/JSONSerializeModule,
    /// 结果是一堆 "Catastrophic failure: FileNotFoundException" 把整个测试类带走(实测)。
    /// 在 [Fact] 里直接把 KeyCode 当普通值用没有这个问题(现有测试都是这么写的)。
    /// </summary>
    public class InputBackendTests
    {
        [Fact]
        public void ToVirtualKey_MapsMouseButtons()
        {
            // Unity 的 Mouse0..Mouse4 ↔ Win32 VK_LBUTTON/VK_RBUTTON/VK_MBUTTON/VK_XBUTTON1/VK_XBUTTON2
            Assert.Equal(0x01, Win32InputBackend.ToVirtualKey(KeyCode.Mouse0));   // 左键
            Assert.Equal(0x02, Win32InputBackend.ToVirtualKey(KeyCode.Mouse1));   // 右键
            Assert.Equal(0x04, Win32InputBackend.ToVirtualKey(KeyCode.Mouse2));   // 中键
            Assert.Equal(0x05, Win32InputBackend.ToVirtualKey(KeyCode.Mouse3));   // 侧键"后退"
            Assert.Equal(0x06, Win32InputBackend.ToVirtualKey(KeyCode.Mouse4));   // 侧键"前进"
        }

        [Fact]
        public void ToVirtualKey_SideButtonsDoNotFallThroughToUnmapped()
        {
            // 回归: Mouse3/Mouse4 曾经没有映射(拿走 default: return 0) → 侧键在 Win32 回退后端静默失效
            Assert.NotEqual(0, Win32InputBackend.ToVirtualKey(KeyCode.Mouse3));
            Assert.NotEqual(0, Win32InputBackend.ToVirtualKey(KeyCode.Mouse4));
        }

        [Fact]
        public void ToVirtualKey_KeepsKeyboardMappingIntact()
        {
            // 顺手确认鼠标分支没有挤掉键盘分支
            Assert.Equal(0x2E, Win32InputBackend.ToVirtualKey(KeyCode.Delete));
            Assert.Equal(0x41, Win32InputBackend.ToVirtualKey(KeyCode.A));
            Assert.Equal(0x70, Win32InputBackend.ToVirtualKey(KeyCode.F1));
            Assert.Equal(0xBD, Win32InputBackend.ToVirtualKey(KeyCode.Minus));
        }

        [Fact]
        public void ToVirtualKey_MapsNumpadKeys()
        {
            // 用户能在 Toys 里把热键设成小键盘键, 兜底后端也得认, 否则设了没反应
            Assert.Equal(0x60, Win32InputBackend.ToVirtualKey(KeyCode.Keypad0));
            Assert.Equal(0x69, Win32InputBackend.ToVirtualKey(KeyCode.Keypad9));
            Assert.Equal(0x6B, Win32InputBackend.ToVirtualKey(KeyCode.KeypadPlus));
            Assert.Equal(0x6D, Win32InputBackend.ToVirtualKey(KeyCode.KeypadMinus));
            Assert.Equal(0x6A, Win32InputBackend.ToVirtualKey(KeyCode.KeypadMultiply));
            Assert.Equal(0x6F, Win32InputBackend.ToVirtualKey(KeyCode.KeypadDivide));
            Assert.Equal(0x6E, Win32InputBackend.ToVirtualKey(KeyCode.KeypadPeriod));
            Assert.Equal(0x0D, Win32InputBackend.ToVirtualKey(KeyCode.KeypadEnter));
        }

        [Fact]
        public void NormalizeScrollAxis_ConvertsUnityAxisUnitsToNotches()
        {
            // Unity 轴 "Mouse ScrollWheel" 默认灵敏度是每格 0.1, 而 mouseScrollDelta 是每格 ±1;
            // 统一折算成"格数", 调用方(自由相机 FOV/距离/高度)才能共用同一套步进。
            Assert.Equal(1f, UnityInputReflectionBackend.NormalizeScrollAxis(0.1f), 3);
            Assert.Equal(-1f, UnityInputReflectionBackend.NormalizeScrollAxis(-0.1f), 3);
            Assert.Equal(2f, UnityInputReflectionBackend.NormalizeScrollAxis(0.2f), 3);
            Assert.Equal(-2f, UnityInputReflectionBackend.NormalizeScrollAxis(-0.2f), 3);
            Assert.Equal(0f, UnityInputReflectionBackend.NormalizeScrollAxis(0f), 3);
        }

        [Fact]
        public void NormalizeScrollAxis_KeepsAlreadyNotchSizedValues()
        {
            // 轴灵敏度被改大、或平台本来就直接给格数: 绝对值 ≥ 0.5 原样返回, 不要再乘 10
            Assert.Equal(0.5f, UnityInputReflectionBackend.NormalizeScrollAxis(0.5f), 3);
            Assert.Equal(1f, UnityInputReflectionBackend.NormalizeScrollAxis(1f), 3);
            Assert.Equal(-3f, UnityInputReflectionBackend.NormalizeScrollAxis(-3f), 3);
        }

        [Fact]
        public void NormalizeScrollAxis_RejectsNonFiniteValues()
        {
            // 反射拿到的值不可信: NaN/Inf 不能顺着算术流到相机高度上
            Assert.Equal(0f, UnityInputReflectionBackend.NormalizeScrollAxis(float.NaN), 3);
            Assert.Equal(0f, UnityInputReflectionBackend.NormalizeScrollAxis(float.PositiveInfinity), 3);
            Assert.Equal(0f, UnityInputReflectionBackend.NormalizeScrollAxis(float.NegativeInfinity), 3);
        }
    }
}

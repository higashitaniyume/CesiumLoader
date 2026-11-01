using System;
using Core.Scene;
using Tools;
using UI;

namespace CesiumLoader.SDK.Mods
{
    /// <summary>
    /// "游戏是否已经启动完成"的只读探测。
    ///
    /// 硬性约束: <b>只读, 绝不触发懒构造</b>。
    ///  - <see cref="SimpleSingletonProvider{T}"/> 的 <c>inst</c> 是懒构造: 取不到就 <c>new T()</c>,
    ///    启动早期调用会造出一个游戏自己都不认识的半成品单例, 之后游戏按自己的实例继续跑,
    ///    结果是原生段直接终止(0x80000003 / 0xC0000005), 托管侧 try/catch 救不回来。
    ///    所以这里一律先用 <c>hasInstance</c>(纯静态字段判空)确认游戏已经自己建好, 再读实例。
    ///  - 所有成员都在主线程被调用(由 <see cref="StartupGate"/> 的 UniTask 续体保证)。
    ///
    /// 判定为"已就绪"的条件: 已经进入可玩场景(不是 None/Login) 且 已经有一个面板打开。
    /// 这两条一起排除了"场景正在切、面板刚被卸掉"的中间态 —— 旧实现里那 30 秒同时覆盖了这两个阶段。
    /// </summary>
    internal static class GameBootProbe
    {
        /// <summary>游戏是否已经启动到可玩状态。任何异常都按"未就绪"处理。</summary>
        public static bool IsBooted()
        {
            try
            {
                if (!SimpleSingletonProvider<SceneManager>.hasInstance) return false;

                SceneManager scenes = SimpleSingletonProvider<SceneManager>.inst;
                if (scenes == null || scenes.currentType == null) return false;
                if (!IsPlayableScene(scenes.currentType.Value)) return false;

                if (!SimpleSingletonProvider<UIManager>.hasInstance) return false;

                UIManager ui = SimpleSingletonProvider<UIManager>.inst;
                return ui != null && ui.currentPanel != null;
            }
            catch { return false; }
        }

        /// <summary>当前场景类型是否已经过了启动阶段(登录/无场景都算没就绪)。</summary>
        private static bool IsPlayableScene(SceneType type)
        {
            switch (type)
            {
                case SceneType.Home:
                case SceneType.Battle:
                case SceneType.SinglePlayer:
                case SceneType.SelectMusic:
                case SceneType.Music:
                    return true;
                default:
                    return false;
            }
        }
    }
}

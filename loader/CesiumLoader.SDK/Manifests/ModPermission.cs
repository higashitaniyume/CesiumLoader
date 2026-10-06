using System;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Speed;

namespace CesiumLoader.SDK.Manifests
{
    public enum ModPermission
    {
        /// <summary>无特殊权限(默认)。</summary>
        None = 0,

        /// <summary>读取对局状态(玩家/手牌/事件订阅)。默认授予, 只读无副作用。</summary>
        ReadGameState = 1 << 0,

        /// <summary>向服务器发送操作 (GameActions: 投骰/移动/用牌等)。
        /// 敏感: 会真实影响对局, 默认关闭。</summary>
        GameActions = 1 << 1,

        /// <summary>变速 (SpeedHack: 改变游戏时间流速)。
        /// 敏感: 联机对局可能触发服务器检测, 默认关闭。</summary>
        SpeedHack = 1 << 2,

        /// <summary>写文件 (mods 目录内配置/日志)。默认授予, 限 mods 目录。</summary>
        FileWrite = 1 << 3,

        /// <summary>修改对局/游戏状态(内存态改写, 不经过服务器 RPC)。比 GameActions 更危险。</summary>
        ModifyGameState = 1 << 4,

        /// <summary>相机控制(创建/驱动/接管相机, 改变渲染视角)。</summary>
        Camera = 1 << 5,

        /// <summary>读取与独占输入(键盘/鼠标)。</summary>
        Input = 1 << 6,

        /// <summary>创建 mod UI(叠加层/窗口/通知)。</summary>
        UI = 1 << 7,

        /// <summary>文件系统访问(mods 目录之外的读写)。</summary>
        FileSystem = 1 << 8,

        /// <summary>网络访问(自行发起连接/请求, 与游戏流量无关)。</summary>
        Network = 1 << 9,

        /// <summary>调试能力(调试叠加层/诊断转储/暂停与步进)。</summary>
        Debug = 1 << 10,
    }
}

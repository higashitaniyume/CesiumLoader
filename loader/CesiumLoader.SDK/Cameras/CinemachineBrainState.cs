using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK.Cameras
{
    public struct CinemachineBrainState
    {
        /// <summary>Cinemachine 是否可用。</summary>
        public bool Available;

        /// <summary>是否找到 Brain。</summary>
        public bool BrainFound;

        /// <summary>Brain 当时是否启用。</summary>
        public bool BrainEnabled;

        /// <summary>接管中的 VirtualCamera 名字(可能为空)。</summary>
        public string ActiveVirtualCameraName;

        /// <summary>当时是否正在混合。</summary>
        public bool IsBlending;

        /// <summary>我们创建的临时 VirtualCamera 名字(用于还原时销毁)。</summary>
        public string CreatedVirtualCameraName;

        /// <summary>摘要文本。</summary>
        public override string ToString()
        {
            if (!Available) return "Cinemachine(不可用)";
            if (!BrainFound) return "Cinemachine(未找到 Brain)";
            return "Cinemachine[brain enabled=" + BrainEnabled +
                   " active=" + (string.IsNullOrEmpty(ActiveVirtualCameraName) ? "(无)" : ActiveVirtualCameraName) +
                   " blending=" + IsBlending + "]";
        }
    }
}

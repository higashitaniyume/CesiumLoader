using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Cameras;
using CesiumLoader.SDK.Configuration;

namespace CesiumLoader.SDK.Diagnostics
{
    public sealed class CameraDump
    {
        /// <summary>生成时刻(UTC)。</summary>
        public string CapturedUtc;

        /// <summary>相机数量。</summary>
        public int CameraCount;

        /// <summary>是否存在主相机。</summary>
        public bool HasMainCamera;

        /// <summary>主相机名。</summary>
        public string MainCameraName;

        /// <summary>主相机层级路径。</summary>
        public string MainCameraPath;

        /// <summary>主相机状态。</summary>
        public CameraState MainCameraState;

        /// <summary>是否检测到 Cinemachine。</summary>
        public bool CinemachineAvailable;

        /// <summary>Cinemachine 是否已初始化。</summary>
        public bool CinemachineInitialized;

        /// <summary>接管中的 VirtualCamera。</summary>
        public string ActiveVirtualCamera;

        /// <summary>Brain 是否启用。</summary>
        public bool BrainEnabled;

        /// <summary>是否正在混合。</summary>
        public bool BrainBlending;

        /// <summary>每台相机的一行摘要。</summary>
        public List<string> Cameras = new List<string>();

        /// <summary>Cinemachine 明细文本。</summary>
        public string CinemachineDetail;

        /// <summary>多行文本。</summary>
        public string ToText()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("===== CameraDump =====");
            sb.AppendLine("时间(UTC)      : " + CapturedUtc);
            sb.AppendLine("相机数量       : " + CameraCount + "   主相机: " + (HasMainCamera ? "有" : "无"));
            if (HasMainCamera)
            {
                sb.AppendLine("主相机         : " + (MainCameraPath ?? MainCameraName ?? "(未知)"));
                sb.AppendLine("主相机状态     : " + MainCameraState.Describe());
            }
            sb.AppendLine("Cinemachine    : 可用=" + CinemachineAvailable + " 初始化=" + CinemachineInitialized +
                          " Brain启用=" + BrainEnabled + " 混合中=" + BrainBlending);
            sb.AppendLine("接管 VirtualCam: " + (ActiveVirtualCamera ?? "(无)"));
            for (int i = 0; i < Cameras.Count; i++) sb.AppendLine("  - " + Cameras[i]);
            if (!string.IsNullOrEmpty(CinemachineDetail))
            {
                sb.AppendLine("Cinemachine 明细:");
                sb.AppendLine(CinemachineDetail);
            }
            return sb.ToString();
        }

        /// <summary>JSON 文本。</summary>
        public string ToJson() { return CesiumJson.SerializePretty(this); }

        /// <summary>摘要。</summary>
        public override string ToString() { return ToText(); }
    }
}

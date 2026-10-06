using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Configuration;

namespace CesiumLoader.SDK.Diagnostics
{
    public sealed class SceneDump
    {
        /// <summary>生成时刻(UTC)。</summary>
        public string CapturedUtc;

        /// <summary>已加载场景数。</summary>
        public int SceneCount;

        /// <summary>活动场景名。</summary>
        public string ActiveSceneName;

        /// <summary>活动场景句柄。</summary>
        public int ActiveSceneHandle;

        /// <summary>已加载场景名列表。</summary>
        public List<string> Scenes = new List<string>();

        /// <summary>活动场景根对象数。</summary>
        public int RootObjectCount;

        /// <summary>根对象名(截断到前 60 个)。</summary>
        public List<string> RootObjects = new List<string>();

        /// <summary>场景加载订阅数。</summary>
        public int SceneSubscribers;

        /// <summary>已转发场景加载次数。</summary>
        public long ForwardedLoads;

        /// <summary>已转发活动场景切换次数。</summary>
        public long ForwardedActiveChanges;

        /// <summary>多行文本。</summary>
        public string ToText()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("===== SceneDump =====");
            sb.AppendLine("时间(UTC)      : " + CapturedUtc);
            sb.AppendLine("已加载场景     : " + SceneCount + " 个   活动: " + (ActiveSceneName ?? "(无)") +
                          " (handle=" + ActiveSceneHandle + ")");
            for (int i = 0; i < Scenes.Count; i++) sb.AppendLine("  - " + Scenes[i]);
            sb.AppendLine("根对象         : " + RootObjectCount + " 个");
            for (int i = 0; i < RootObjects.Count; i++) sb.AppendLine("  - " + RootObjects[i]);
            sb.AppendLine("订阅/转发      : 订阅=" + SceneSubscribers +
                          " 加载转发=" + ForwardedLoads + " 活动场景转发=" + ForwardedActiveChanges);
            return sb.ToString();
        }

        /// <summary>JSON 文本。</summary>
        public string ToJson() { return CesiumJson.SerializePretty(this); }

        /// <summary>摘要。</summary>
        public override string ToString() { return ToText(); }
    }
}

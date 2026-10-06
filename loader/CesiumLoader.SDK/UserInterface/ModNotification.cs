using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.UserInterface
{
    /// <summary>一条 Mod 通知。</summary>
    public struct ModNotification
    {
        /// <summary>自增 id。</summary>
        public long Id;

        /// <summary>发起 mod。</summary>
        public string ModId;

        /// <summary>文本。</summary>
        public string Text;

        /// <summary>级别。</summary>
        public UiNotificationLevel Level;

        /// <summary>创建时刻(realtimeSinceStartup)。</summary>
        public float CreatedAt;

        /// <summary>存活秒数(&lt;=0 表示直到手动清除)。</summary>
        public float Ttl;

        /// <summary>是否已过期。</summary>
        public bool IsExpired;

        /// <summary>剩余秒数。</summary>
        public float Remaining(float now)
        {
            if (Ttl <= 0f) return float.MaxValue;
            return Math.Max(0f, Ttl - (now - CreatedAt));
        }

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "[" + Level + "] " + (ModId ?? "?") + ": " + Text;
        }
    }
}

using System;
using System.Collections.Generic;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.model;
using party.protocol;
using Tools;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>服务器 1002 推送的一条原始动作(<see cref="GameEvents.RawAction"/>)。</summary>
    public sealed class RawActionEvent
    {
        /// <summary>动作类型 id(与 cmd 号同空间, 如 5021 掷骰 / 5027 移动 / 5211 筹码候选 / 5249 买筹码 offer)。</summary>
        public int Id;
        /// <summary>动作序列号(同一动作会被服务器重复广播, 按它去重)。</summary>
        public long Sn;
        /// <summary>动作发起者。</summary>
        public long PlayerId;
        /// <summary>protobuf payload(可能为 null); 用 <c>Core.Net.ByteBuf.ReadObject&lt;T&gt;(Data)</c> 解码。</summary>
        public byte[] Data;

        public override string ToString()
        {
            return "Action#" + Id + "(sn=" + Sn + ", pid=" + PlayerId +
                   ", len=" + (Data == null ? 0 : Data.Length) + ")";
        }
    }
}

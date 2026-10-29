using System;
using System.Collections;
using System.Collections.Generic;

namespace QuickChatPlusMod
{
    /// <summary>
    /// 一次列表扩展的全部改动，Restore 时按这份记录原样回滚：
    /// 追加的克隆、被临时放开的地图限定。
    /// </summary>
    public sealed class ChatListMutation
    {
        /// <summary>追加的克隆条数。</summary>
        public int Added;

        /// <summary>被放开地图限定的原条目数。</summary>
        public int Unlimited;

        /// <summary>各目标模式扩展前后的短语数量，例如 “PVE 19→32，词条PVE 16→32”。</summary>
        public string Summary;

        internal readonly List<object> Clones = new List<object>();
        internal readonly List<LimitPatch> LimitPatches = new List<LimitPatch>();

        public bool IsEmpty { get { return Added == 0 && Unlimited == 0; } }

        internal struct LimitPatch
        {
            public ChatInfoConfigure Info;
            public int[] Values;
        }
    }

    /// <summary>
    /// 把 Chat 配置里“属于其他类别/其他模式”的既有短语补进 <see cref="ChatConfigure.Infos"/>，
    /// 并临时清空原条目的 MapLimit，让按地图限定（例如只在 82010 出现）的短语也能在所有地图选中。
    ///
    /// 只操作传入的配置对象，不依赖 Unity / 单例，便于在离线测试里用真实
    /// <see cref="ChatConfigure.GetBattleChats"/> 验证结果。
    /// </summary>
    public static class ChatListExpander
    {
        public static ChatListMutation Expand(ChatConfigure chat, int[] targetModes,
            bool includeRoom, bool includeResult)
        {
            var mutation = new ChatListMutation();
            try
            {
                ExpandCore(chat, targetModes, includeRoom, includeResult, mutation);
                return mutation;
            }
            catch
            {
                // 追加中途失败时先把自己已经做的改动回滚，避免留下半套克隆。
                Restore(chat, mutation);
                throw;
            }
        }

        private static void ExpandCore(ChatConfigure chat, int[] targetModes,
            bool includeRoom, bool includeResult, ChatListMutation mutation)
        {
            if (chat == null || targetModes == null || targetModes.Length == 0) return;

            IList infos = chat.Infos;
            // 配置尚未加载完：返回空改动，让调用方下一帧再试。
            if (infos == null || infos.Count == 0) return;

            int count = infos.Count;
            var entries = new List<PhraseEntry>(count);
            var sources = new List<ChatInfoConfigure>(count);
            for (int i = 0; i < count; i++)
            {
                var info = (ChatInfoConfigure)infos[i];
                sources.Add(info);
                entries.Add(ToEntry(info));
            }

            // 先放开地图限定，Summary 里的“扩展前数量”才与实际列表一致。
            for (int i = 0; i < sources.Count; i++)
            {
                if (!TouchesAnyTargetMode(entries[i].Modes, targetModes)) continue;
                var limits = (IList)sources[i].MapLimit;
                if (limits == null || limits.Count == 0) continue;
                var values = new int[limits.Count];
                for (int k = 0; k < values.Length; k++) values[k] = (int)limits[k];
                limits.Clear();
                mutation.LimitPatches.Add(new ChatListMutation.LimitPatch { Info = sources[i], Values = values });
                mutation.Unlimited++;
            }

            var additions = PhrasePlanner.Plan(entries, targetModes, includeRoom, includeResult);
            for (int i = 0; i < additions.Count; i++)
            {
                var addition = additions[i];
                var copy = sources[addition.EntryIndex].Clone();
                var types = (IList)copy.ChatType;
                types.Clear();
                types.Add(ChatType.InGame);
                var mapModes = (IList)copy.MapModeType;
                mapModes.Clear();
                mapModes.Add((MapModeType)addition.Mode);
                ((IList)copy.MapLimit).Clear();

                infos.Add(copy);
                mutation.Clones.Add(copy);
            }

            mutation.Added = additions.Count;
            mutation.Summary = Summarize(targetModes, entries, additions);
        }

        /// <summary>按引用移除追加的克隆，并恢复被放开的 MapLimit。</summary>
        public static void Restore(ChatConfigure chat, ChatListMutation mutation)
        {
            if (mutation == null) return;

            if (chat != null)
            {
                IList infos = chat.Infos;
                if (infos != null)
                {
                    for (int i = 0; i < mutation.Clones.Count; i++)
                    {
                        var target = mutation.Clones[i];
                        if (target == null) continue;
                        for (int j = infos.Count - 1; j >= 0; j--)
                        {
                            if (ReferenceEquals(infos[j], target))
                            {
                                infos.RemoveAt(j);
                                break;
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < mutation.LimitPatches.Count; i++)
            {
                var patch = mutation.LimitPatches[i];
                if (patch.Info == null) continue;
                var limits = (IList)patch.Info.MapLimit;
                if (limits == null) continue;
                limits.Clear();
                if (patch.Values == null) continue;
                for (int k = 0; k < patch.Values.Length; k++) limits.Add(patch.Values[k]);
            }

            mutation.Clones.Clear();
            mutation.LimitPatches.Clear();
        }

        private static bool TouchesAnyTargetMode(int[] modes, int[] targetModes)
        {
            for (int i = 0; i < targetModes.Length; i++)
            {
                if (PhrasePlanner.Contains(modes, targetModes[i])) return true;
            }
            return false;
        }

        private static PhraseEntry ToEntry(ChatInfoConfigure info)
        {
            return new PhraseEntry
            {
                ChatId = info.ChatID,
                InGame = HasType(info.ChatType, ChatType.InGame),
                Room = HasType(info.ChatType, ChatType.Room),
                Result = HasType(info.ChatType, ChatType.Result),
                Modes = ToModeArray(info.MapModeType),
            };
        }

        private static bool HasType(IList types, ChatType expected)
        {
            if (types == null) return false;
            for (int i = 0; i < types.Count; i++)
            {
                if (types[i] is ChatType value && value == expected) return true;
            }
            return false;
        }

        private static int[] ToModeArray(IList modes)
        {
            if (modes == null) return new int[0];
            var result = new int[modes.Count];
            for (int i = 0; i < result.Length; i++) result[i] = (int)(MapModeType)modes[i];
            return result;
        }

        private static string Summarize(int[] modes, List<PhraseEntry> entries, List<PhraseAddition> additions)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < modes.Length; i++)
            {
                int mode = modes[i];
                var seen = new HashSet<int>();
                int before = 0;
                for (int e = 0; e < entries.Count; e++)
                {
                    var entry = entries[e];
                    if (entry != null && entry.InGame && PhrasePlanner.Contains(entry.Modes, mode) &&
                        seen.Add(entry.ChatId)) before++;
                }
                int added = 0;
                for (int a = 0; a < additions.Count; a++)
                    if (additions[a].Mode == mode) added++;
                if (i > 0) text.Append("，");
                text.Append(ModeName(mode)).Append(' ').Append(before).Append("→").Append(before + added);
            }
            return text.ToString();
        }

        private static string ModeName(int mode)
        {
            switch ((MapModeType)mode)
            {
                case MapModeType.Pve: return "PVE";
                case MapModeType.MutatorPve: return "词条PVE";
                case MapModeType.CampaignPve: return "战役PVE";
                case MapModeType.PracticePve: return "练习PVE";
                case MapModeType.Standard: return "标准PVP";
                case MapModeType.CampaignPvp: return "战役PVP";
                case MapModeType.AsymmetricalBattle: return "非对称";
                case MapModeType.PracticePvp: return "练习PVP";
                case MapModeType.Ultra: return "无限火力";
                case MapModeType.LuckyStarBattle: return "幸运星";
                default: return "模式" + mode;
            }
        }
    }
}

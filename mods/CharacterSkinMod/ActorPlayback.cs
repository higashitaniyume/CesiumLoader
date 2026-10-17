#nullable disable
using System;
using CesiumLoader.CharacterSkins;

namespace CharacterSkinMod
{
    // 一帧的取图结论。Mapping：按原精灵逐帧替换；Sequence：正在播自定义整段序列；
    // HoldCustom：沿用最近一张自定义帧（保住换皮外观）；HoldApplied：读不到原精灵名，
    // 继续显示上一张自定义精灵。MissingAction 非空表示该动作完全没有自定义内容。
    // LayoutName 指向"该自定义帧对应哪一张原帧"：变体动作和沿用帧必须用那一帧的
    // pivot/PPU，否则同一动作里会在两套锚点之间来回跳，表现为小人物位置抖动。
    internal enum PlaybackKind { Mapping, Sequence, HoldCustom, HoldApplied }
    internal struct PlaybackDecision
    {
        public PlaybackKind Kind;
        public string File;
        public bool OriginalLayout;
        public string LayoutName;
        public string MissingAction;
    }
    // 纯状态机：只决定这一帧该显示哪张自定义图片，不接触 Unity 对象，便于离线测试。
    // 关键约束：游戏自己的 Animator/Playable 才是动画时序的唯一来源。原精灵有逐帧
    // 映射时直接跟随游戏当前帧；只有整段自定义序列才使用本地时钟，并按游戏当前
    // 动画速率推进（速率 0 表示游戏已暂停/时停，此时同样冻结）。
    internal sealed class ActorPlayback
    {
        public string Action = "";
        public string LastCustomFile = "";
        public string LastCustomName = "";  // 最近一张自定义帧对应的原精灵名（含变体换算）
        public double Elapsed;      // 自定义序列已播放毫秒数
        public double LastTime;     // 上一帧的 Time.time
        public int LastIndex = -1;  // 上一帧原精灵名的帧号

        public void Reset() { Action = ""; LastCustomFile = ""; LastCustomName = ""; Elapsed = 0; LastTime = 0; LastIndex = -1; }

        internal static CharacterAnimation Find(CharacterSkinManifest manifest, string action)
        {
            if (manifest == null || manifest.Animations == null || string.IsNullOrEmpty(action)) return null;
            foreach (var pair in manifest.Animations)
                if (string.Equals(pair.Key, action, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }
        private static bool Frames(CharacterAnimation animation) { return animation != null && animation.Frames != null && animation.Frames.Count > 0; }
        private static bool Mapping(CharacterSkinManifest manifest, string spriteName, out string file)
        {
            file = "";
            return manifest.SpriteMappings != null && !string.IsNullOrEmpty(spriteName)
                && manifest.SpriteMappings.TryGetValue(spriteName, out file) && !string.IsNullOrEmpty(file);
        }
        private static PlaybackDecision Decision(PlaybackKind kind, string file, bool originalLayout, string layoutName, string missing)
        {
            return new PlaybackDecision { Kind = kind, File = file, OriginalLayout = originalLayout, LayoutName = layoutName, MissingAction = missing };
        }
        public PlaybackDecision Step(CharacterSkinManifest manifest, string spriteName, double now, float rate)
        {
            // 读不到原精灵名时不猜动作，保留上一张自定义精灵。
            if (manifest == null || string.IsNullOrEmpty(spriteName)) return Decision(PlaybackKind.HoldApplied, "", false, "", "");
            string action = AnimationPlayback.ActionName(spriteName);
            int index = AnimationPlayback.FrameIndex(spriteName);
            double previousTime = LastTime;
            bool sameAction = string.Equals(Action, action, StringComparison.Ordinal);
            // 同一动作里原帧号回退说明游戏把它重播了一遍。自由时钟若不在此复位，
            // 非循环动作第二次连播会整段停在最后一帧。
            bool restarted = sameAction && index >= 0 && LastIndex >= 0 && index < LastIndex;
            double advanced = AnimationPlayback.Advance(Elapsed, now - previousTime, rate);
            LastTime = now; LastIndex = index;

            // 1) 原精灵有逐帧映射：直接跟随游戏当前帧。帧序、房间速度、回放倍速、走路
            //    速度倍率、时停与切换时机全部由游戏的 Animator/Playable 决定。时钟继续
            //    累加，供之后切到没有内容的动作时按正确位置继续自定义序列。
            if (Mapping(manifest, spriteName, out var mapped))
            {
                Elapsed = sameAction && !restarted ? advanced : 0;
                Action = action; LastCustomFile = mapped; LastCustomName = spriteName;
                return Decision(PlaybackKind.Mapping, mapped, manifest.UseOriginalSpriteLayout, spriteName, "");
            }
            // 2) 变体动作名：同一动作在其它皮肤/变体里的精灵名带后缀（Idle-02、Talent-03、
            //    Fight-attack2），帧号通常与基础动作一一对应，按帧号取基础动作的自定义帧，
            //    锚点也用基础动作那一帧的（LayoutName），保证与基础动作完全一致。
            string variant = AnimationPlayback.VariantBase(action);
            if (variant.Length > 0)
            {
                string variantKey = AnimationPlayback.VariantSprite(variant, spriteName);
                if (Mapping(manifest, variantKey, out var variantMapped))
                {
                    Elapsed = sameAction && !restarted ? advanced : 0;
                    Action = action; LastCustomFile = variantMapped; LastCustomName = variantKey;
                    return Decision(PlaybackKind.Mapping, variantMapped, manifest.UseOriginalSpriteLayout, variantKey, "");
                }
            }

            if (!sameAction)
            {
                // 上一段非循环自定义序列还没播完时先播完它，再接管游戏当前动作。
                if (!string.IsNullOrEmpty(Action) && AnimationPlayback.ShouldFinish(Find(manifest, Action), advanced)) Elapsed = advanced;
                else { Action = action; Elapsed = 0; LastCustomFile = ""; LastCustomName = ""; }
            }
            else if (restarted) { Elapsed = 0; LastCustomFile = ""; LastCustomName = ""; }
            else Elapsed = advanced;

            // 3) 当前动作的整段自定义序列。
            var animation = Find(manifest, Action);
            if (Frames(animation))
                return Decision(PlaybackKind.Sequence, animation.Frames[AnimationPlayback.Frame(animation, Elapsed)], false, "", "");
            // 4) 变体动作的基础动作序列（例如 Fight-attack2 走 Fight-attack 的自定义动作）。
            if (variant.Length > 0)
            {
                var baseAnimation = Find(manifest, variant);
                if (Frames(baseAnimation))
                    return Decision(PlaybackKind.Sequence, baseAnimation.Frames[AnimationPlayback.Frame(baseAnimation, Elapsed)], false, "", "");
            }
            // 5) 兜底自定义 Idle 序列：宁可播错动作也不让换皮角色露出原版外观。
            if (!string.Equals(Action, "Idle", StringComparison.OrdinalIgnoreCase))
            {
                var idle = Find(manifest, "Idle");
                if (Frames(idle))
                    return Decision(PlaybackKind.Sequence, idle.Frames[AnimationPlayback.Frame(idle, Elapsed)], false, "", "");
            }
            // 6) 沿用最近一张自定义帧。该动作已有映射，只是这一帧缺失（例如游戏更新后
            //    新增了原帧），不算整个动作缺内容，所以不写警告。锚点用那张帧自己的。
            if (!string.IsNullOrEmpty(LastCustomFile))
                return Decision(PlaybackKind.HoldCustom, LastCustomFile, manifest.UseOriginalSpriteLayout, LastCustomName, "");
            // 7) 这个动作确实没有任何自定义内容可播，保留上一张自定义精灵并上报动作名。
            return Decision(PlaybackKind.HoldApplied, "", false, "", Action);
        }
    }
}

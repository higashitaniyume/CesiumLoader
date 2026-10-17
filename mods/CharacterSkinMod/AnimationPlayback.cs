using System;
using CesiumLoader.CharacterSkins;

namespace CharacterSkinMod
{
    internal static class AnimationPlayback
    {
        internal static string ActionName(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return "";
            int split = spriteName.LastIndexOf('_');
            if (split <= 0 || split == spriteName.Length - 1) return spriteName;
            for (int i = split + 1; i < spriteName.Length; i++)
                if (spriteName[i] < '0' || spriteName[i] > '9') return spriteName;
            return spriteName.Substring(0, split);
        }
        // 原精灵名的帧号。用于识别游戏把同一个动作又播了一遍（帧号回退）。
        internal static int FrameIndex(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return -1;
            int split = spriteName.LastIndexOf('_');
            if (split <= 0 || split == spriteName.Length - 1) return -1;
            int value = 0;
            for (int i = split + 1; i < spriteName.Length; i++)
            {
                char c = spriteName[i];
                if (c < '0' || c > '9') return -1;
                value = value * 10 + (c - '0');
                if (value > 1000000) return -1;
            }
            return value;
        }
        // 游戏会给同一动作的其它皮肤/变体加后缀，例如 Idle-02、Talent-03、Fight-attack2。
        // 去掉这个变体后缀得到基础动作名；没有后缀时返回空串。
        internal static string VariantBase(string action)
        {
            if (string.IsNullOrEmpty(action)) return "";
            int end = action.Length;
            while (end > 0 && action[end - 1] >= '0' && action[end - 1] <= '9') end--;
            if (end == action.Length) return "";
            if (end > 0 && (action[end - 1] == '-' || action[end - 1] == '_')) end--;
            return end > 0 && end < action.Length ? action.Substring(0, end) : "";
        }
        // 用基础动作名 + 原精灵名的帧号后缀拼出对照精灵名：Idle-02_00007 -> Idle_00007。
        internal static string VariantSprite(string action, string spriteName)
        {
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(spriteName)) return "";
            int split = spriteName.LastIndexOf('_');
            if (split <= 0 || split == spriteName.Length - 1) return "";
            return action + spriteName.Substring(split);
        }
        // 自定义序列的时钟必须跟随游戏当前动画速率（房间速度、回放倍速、走路速度
        // 倍率），否则游戏把动画调快时自制帧会明显滞后。Unity 对动画更新使用的
        // deltaTime 有上限（Time.maximumDeltaTime，默认约 1/3 秒），这里同样限幅。
        internal static double Advance(double elapsedMs, double deltaSeconds, float rate)
        {
            if (rate <= 0f || deltaSeconds <= 0) return elapsedMs;
            if (deltaSeconds > 0.5) deltaSeconds = 0.5;
            return elapsedMs + deltaSeconds * rate * 1000.0;
        }
        internal static double Length(CharacterAnimation animation)
        {
            double total = 0;
            for (int i = 0; i < animation.Frames.Count; i++) total += Duration(animation, i);
            return total;
        }
        private static double Duration(CharacterAnimation animation, int frame)
        {
            return animation.DurationsMs.Count == 0 ? 1000.0 / animation.Fps : animation.DurationsMs[frame];
        }
        internal static bool ShouldFinish(CharacterAnimation animation, double elapsed)
        {
            return animation != null && !animation.Loop && elapsed < Length(animation);
        }
        internal static int Frame(CharacterAnimation animation, double elapsed)
        {
            double length = Length(animation);
            elapsed = Math.Max(0, elapsed);
            elapsed = animation.Loop ? elapsed % length : Math.Min(elapsed, length - 0.001);
            int frame = 0;
            while (frame < animation.Frames.Count - 1)
            {
                double duration = Duration(animation, frame);
                if (elapsed < duration) break;
                elapsed -= duration;
                frame++;
            }
            return frame;
        }
    }
}

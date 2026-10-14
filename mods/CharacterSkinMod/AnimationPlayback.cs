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

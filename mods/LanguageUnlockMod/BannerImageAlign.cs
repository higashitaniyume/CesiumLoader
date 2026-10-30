using Google.Protobuf.Collections;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 横幅图片对齐。
    ///
    /// <c>BannerInfo.BannerImages</c> 固定 4 个槽位：简中 / 英 / 日 / 繁，
    /// 游戏按当前语言取对应槽位（<c>GetDataForLanguage(images[1], images[2], images[0], images[3])</c>）。
    /// 国服只下发了简中横幅图，切到英 / 日 / 繁时游戏会去加载国服没有的 Addressables key，
    /// 首页横幅出现空图并刷大量 <c>InvalidKeyException</c>。
    ///
    /// 这里把三个外语槽位指回简中图：仍显示国服素材，但不再报错、不再空白。
    /// </summary>
    internal static class BannerImageAlign
    {
        internal static int Align(RepeatedField<string> images)
        {
            if (images == null || images.Count < 4) return 0;

            string simplified = images[0];
            if (string.IsNullOrEmpty(simplified)) return 0;

            int changed = 0;
            for (int i = 1; i < 4; i++)
            {
                if (images[i] == simplified) continue;
                images[i] = simplified;
                changed++;
            }
            return changed;
        }
    }
}

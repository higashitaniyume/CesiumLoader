using System;
using CesiumLoader.SDK.Logging;
using Core;
using Cysharp.Threading.Tasks;
using Tools;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 字体预检。
    ///
    /// <c>UIManager.AsyncLoadFonts</c> 里 TMP 字体是 <c>await</c> 的：只要目标语言的某个字体
    /// 资源取不到，登录场景的 <c>AsyncInit</c> 就会抛异常、停在登录界面。切换前先把目标语言的
    /// 字体全部加载一遍，取不到的条目回退成简中字体字段，保证登录流程不会因为字体中断。
    /// </summary>
    internal static class FontProbe
    {
        internal static async UniTask<int> EnsureAsync(LanguageType language, ModLogger log)
        {
            string property = LanguageCodes.FontKeyProperty(language);
            if (property == null) return 0;

            var config = StaticConfigure.Font;
            if (config == null) return 0;

            var infos = config.Infos;
            if (infos == null) return 0;

            int remapped = 0;
            for (int i = 0; i < infos.Count; i++)
            {
                FontInfoConfigure info = infos[i];
                if (info == null) continue;

                string key = Reflective.GetString(info, property);
                if (string.IsNullOrEmpty(key)) continue;

                // 与 AsyncLoadFonts 的分支保持一致：只有 TMP 字体是 await 的。
                string name = info.Name ?? string.Empty;
                if (name.IndexOf("_TMP", StringComparison.Ordinal) < 0) continue;
                if (key.IndexOf("_TMP", StringComparison.Ordinal) < 0) continue;

                if (await CanLoadAsync(key)) continue;

                string fallback = info.LoadedKeyCN;
                if (string.IsNullOrEmpty(fallback) || !await CanLoadAsync(fallback))
                {
                    if (log != null) log.Warn("字体不可用且没有简中回退: " + key);
                    continue;
                }

                if (Reflective.Set(info, property, fallback))
                {
                    remapped++;
                    if (log != null) log.Warn("字体 " + key + " 不可用, 回退到 " + fallback);
                }
            }

            return remapped;
        }

        private static async UniTask<bool> CanLoadAsync(string key)
        {
            try
            {
                AsyncOperationHandle<UnityEngine.Object> handle =
                    await AddressableHelper.LoadAssetAsync<UnityEngine.Object>(key);
                Addressables.Release(handle);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}


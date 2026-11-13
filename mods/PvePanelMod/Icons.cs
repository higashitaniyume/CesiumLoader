using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>
    /// 面板自己的小图标（data/*.png，随包数据目录）。写法照 CardSkinMod.CardTextureLoader：
    /// 编译期拿不到 ImageConversion，运行时按名字找到 ImageConversion.LoadImage 再调用。
    /// 读不到的图标返回 null，界面那一格就空着，不会报错。
    /// </summary>
    internal sealed class Icons : IDisposable
    {
        private readonly string _dir;
        private readonly Dictionary<string, NTexture> _cache = new Dictionary<string, NTexture>();
        private static MethodInfo _loadImage;
        private static bool _resolved;

        public Icons(string modDirectory)
        {
            _dir = Path.Combine(modDirectory ?? "", "data");
        }

        public NTexture Get(string name)
        {
            NTexture t;
            if (_cache.TryGetValue(name, out t) && t != null && t.nativeTexture != null) return t;
            t = Load(Path.Combine(_dir, name + ".png"));
            _cache[name] = t;
            return t;
        }

        private static NTexture Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                Resolve();
                if (_loadImage == null) return null;
                byte[] bytes = File.ReadAllBytes(file);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                bool ok = (bool)_loadImage.Invoke(null, _loadImage.GetParameters().Length == 3
                    ? new object[] { tex, bytes, false } : new object[] { tex, bytes });
                if (!ok) { UnityEngine.Object.Destroy(tex); return null; }
                return new NTexture(tex) { destroyMethod = DestroyMethod.None };
            }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var t = RuntimeAssemblyService.FindTypeBySimpleName("ImageConversion");
                if (t != null)
                    _loadImage = t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) })
                                 ?? t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
            }
            catch { }
        }

        public void Dispose()
        {
            foreach (var t in _cache.Values)
            {
                try { if (t?.nativeTexture != null) UnityEngine.Object.Destroy(t.nativeTexture); } catch { }
            }
            _cache.Clear();
        }
    }
}

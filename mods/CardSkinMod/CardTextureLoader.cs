using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using UnityEngine;

namespace CardSkinMod
{
    /// <summary>
    /// 卡面纹理加载器：将本地图片文件（PNG/JPG）加载为 FairyGUI NTexture
    /// 双通道兼容：优先使用 ImageConversion.LoadImage，回退使用 UnityWebRequestTexture
    /// </summary>
    public sealed class CardTextureLoader : IDisposable
    {
        // FairyGUI 校验 nativeTexture.name == loader.url；同一文件绑定多个资源名时必须各有纹理。
        private readonly Dictionary<string, NTexture> _textureCache = new Dictionary<string, NTexture>(StringComparer.Ordinal);
        // TextureManager may dispose an NTexture with DestroyMethod.None during a scene change.
        // Keep native ownership independently so this cannot orphan the Unity allocation.
        private readonly Dictionary<string, Texture> _nativeTextures = new Dictionary<string, Texture>(StringComparer.Ordinal);

        // 反射解析方法缓存
        private static bool _methodsResolved;
        private static MethodInfo _loadImageMethod;
        private static MethodInfo _getTextureMethod;

        public static void EnsureMethodsResolved()
        {
            if (_methodsResolved) return;
            _methodsResolved = true;

            try
            {
                // 通道 1: ImageConversion.LoadImage(Texture2D, byte[])
                var imgConvType = RuntimeAssemblyService.FindTypeBySimpleName("ImageConversion");
                if (imgConvType != null)
                {
                    _loadImageMethod = imgConvType.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("CardSkin", "解析 ImageConversion 失败: " + ex.Message);
            }

            try
            {
                // 通道 2: UnityWebRequestTexture.GetTexture(string)
                var uwrTexType = RuntimeAssemblyService.FindTypeBySimpleName("UnityWebRequestTexture");
                if (uwrTexType != null)
                {
                    _getTextureMethod = uwrTexType.GetMethod("GetTexture", new[] { typeof(string) });
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("CardSkin", "解析 UnityWebRequestTexture 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 加载或从缓存中获取文件的 NTexture
        /// </summary>
        public NTexture GetOrCreateTexture(string filePath, string assetKey = null, int expectedWidth = 0, int expectedHeight = 0)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            string cacheKey = (assetKey ?? string.Empty) + "\n" + filePath;
            if (_textureCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached != null && !cached.disposed && cached.nativeTexture != null)
                {
                    return cached;
                }
                DestroyCachedTexture(cacheKey);
            }

            EnsureMethodsResolved();

            NTexture loaded = LoadFromFile(filePath, expectedWidth, expectedHeight);
            if (loaded != null)
            {
                loaded.nativeTexture.name = assetKey ?? string.Empty;
                _textureCache[cacheKey] = loaded;
                _nativeTextures[cacheKey] = loaded.nativeTexture;
            }
            return loaded;
        }

        private static NTexture LoadFromFile(string filePath, int expectedWidth, int expectedHeight)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes == null || bytes.Length == 0) return null;
                if (expectedWidth > 0 && !CardAnimation.ValidPngHeader(bytes, expectedWidth, expectedHeight)) return null;

                // 尝试通道 1: ImageConversion
                if (_loadImageMethod != null)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    bool owned = false;
                    try
                    {
                        bool ok = (bool)_loadImageMethod.Invoke(null, new object[] { tex, bytes });
                        if (ok && tex != null)
                        {
                            var nTex = new NTexture(tex) { destroyMethod = DestroyMethod.None };
                            owned = true;
                            return nTex;
                        }
                    }
                    finally { if (!owned && tex != null) UnityEngine.Object.Destroy(tex); }
                }

                // 尝试通道 2: UnityWebRequestTexture 同步/阻塞读取本地 file://
                if (_getTextureMethod != null)
                {
                    string fileUrl = "file:///" + Path.GetFullPath(filePath).Replace('\\', '/');
                    object req = _getTextureMethod.Invoke(null, new object[] { fileUrl });
                    if (req != null)
                    {
                        try
                        {
                        var sendMethod = req.GetType().GetMethod("SendWebRequest", Type.EmptyTypes);
                        var asyncOp = sendMethod?.Invoke(req, null);
                        if (asyncOp != null)
                        {
                            // 等待本地请求完成 (本地文件通常 < 2ms)
                            var isDoneProp = asyncOp.GetType().GetProperty("isDone");
                            var startTicks = DateTime.UtcNow.Ticks;
                            while (isDoneProp != null && !(bool)isDoneProp.GetValue(asyncOp, null))
                            {
                                if ((DateTime.UtcNow.Ticks - startTicks) > 10000000L) break; // 1s 超时
                            }
                        }

                        var handlerProp = req.GetType().GetProperty("downloadHandler");
                        var handler = handlerProp?.GetValue(req, null);
                        if (handler != null)
                        {
                            var textureProp = handler.GetType().GetProperty("texture");
                            var uTex = textureProp?.GetValue(handler, null) as Texture;
                            if (uTex != null)
                            {
                                var nTex = new NTexture(uTex)
                                {
                                    destroyMethod = DestroyMethod.None
                                };
                                return nTex;
                            }
                        }
                        }
                        finally { (req as IDisposable)?.Dispose(); }
                    }
                }

                SdkLog.Warn("CardSkin", "无法解码图片文件: " + filePath);
                return null;
            }
            catch (Exception ex)
            {
                SdkLog.Error("CardSkin", "加载卡图文件失败: " + filePath + " " + ex.Message);
                return null;
            }
        }

        public void RetainFiles(string first, string second)
        {
            var remove = new List<string>();
            foreach (var pair in _textureCache)
                if (!pair.Key.EndsWith("\n" + first, StringComparison.Ordinal) && !pair.Key.EndsWith("\n" + second, StringComparison.Ordinal)) remove.Add(pair.Key);
            foreach (string key in remove) DestroyCachedTexture(key);
        }

        public bool OwnsTexture(NTexture texture)
        {
            if (texture == null || texture.disposed) return false;
            foreach (var entry in _textureCache)
            {
                if (entry.Value == texture) return true;
            }
            return false;
        }

        private void DestroyCachedTexture(string key)
        {
            try
            {
                if (_textureCache.TryGetValue(key, out var texture) && texture != null && !texture.disposed)
                { texture.destroyMethod = DestroyMethod.None; texture.Dispose(); }
            }
            finally
            {
                if (_nativeTextures.TryGetValue(key, out var native) && native != null) UnityEngine.Object.Destroy(native);
                _nativeTextures.Remove(key);
                _textureCache.Remove(key);
            }
        }

        public void Clear()
        {
            var keys = new List<string>(_textureCache.Keys);
            foreach (string key in keys) { try { DestroyCachedTexture(key); } catch { } }
            _textureCache.Clear();
            _nativeTextures.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}

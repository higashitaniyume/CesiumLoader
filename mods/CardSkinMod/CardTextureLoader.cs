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
        public NTexture GetOrCreateTexture(string filePath, string assetKey = null)
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
                _textureCache.Remove(cacheKey);
            }

            EnsureMethodsResolved();

            NTexture loaded = LoadFromFile(filePath);
            if (loaded != null)
            {
                loaded.nativeTexture.name = assetKey ?? string.Empty;
                _textureCache[cacheKey] = loaded;
            }
            return loaded;
        }

        private static NTexture LoadFromFile(string filePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes == null || bytes.Length == 0) return null;

                // 尝试通道 1: ImageConversion
                if (_loadImageMethod != null)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    bool ok = (bool)_loadImageMethod.Invoke(null, new object[] { tex, bytes });
                    if (ok && tex != null)
                    {
                        var nTex = new NTexture(tex)
                        {
                            destroyMethod = DestroyMethod.None // 保护纹理不被 GLoader 自动释放回收
                        };
                        return nTex;
                    }
                }

                // 尝试通道 2: UnityWebRequestTexture 同步/阻塞读取本地 file://
                if (_getTextureMethod != null)
                {
                    string fileUrl = "file:///" + Path.GetFullPath(filePath).Replace('\\', '/');
                    object req = _getTextureMethod.Invoke(null, new object[] { fileUrl });
                    if (req != null)
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

        public bool OwnsTexture(NTexture texture)
        {
            if (texture == null || texture.disposed) return false;
            foreach (var entry in _textureCache)
            {
                if (entry.Value == texture) return true;
            }
            return false;
        }

        public void Clear()
        {
            foreach (var kvp in _textureCache)
            {
                try
                {
                    if (kvp.Value != null && !kvp.Value.disposed)
                    {
                        kvp.Value.Dispose();
                    }
                }
                catch { }
            }
            _textureCache.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}

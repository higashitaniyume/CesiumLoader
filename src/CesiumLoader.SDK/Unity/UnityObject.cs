using System;
using System.Collections.Generic;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// UnityEngine.Object 的安全访问封装。
    ///
    /// IL2CPP 现实: 被销毁的 UnityEngine.Object 不是 C# null, 直接访问其成员会抛
    /// MissingReferenceException; 跨场景持有的引用随时可能变成"已销毁"。
    /// 所有 SDK 代码都应通过本类做判活/销毁, 不要写裸 <c>obj != null</c>。
    /// </summary>
    public static class UnityObject
    {
        /// <summary>对象存在且未被销毁。</summary>
        public static bool IsAlive(UnityEngine.Object obj)
        {
            if (obj == null) return false;
            try { return obj != null; }
            catch { return false; }
        }

        /// <summary>对象为 null 或已被销毁。</summary>
        public static bool IsNull(UnityEngine.Object obj)
        {
            return !IsAlive(obj);
        }

        /// <summary>安全读取名字。</summary>
        public static string GetName(UnityEngine.Object obj)
        {
            return IsAlive(obj) ? UnityCall.Name(obj) : null;
        }

        /// <summary>安全设置名字。</summary>
        public static bool SetName(UnityEngine.Object obj, string name)
        {
            try
            {
                if (!IsAlive(obj)) return false;
                return UnityCall.SetName(obj, name);
            }
            catch { return false; }
        }

        /// <summary>安全取 instance id(已销毁返回 0)。</summary>
        public static int GetInstanceId(UnityEngine.Object obj)
        {
            return IsAlive(obj) ? UnityCall.InstanceId(obj) : 0;
        }

        /// <summary>
        /// 延迟销毁对象。必须在主线程调用。
        /// <paramref name="delaySeconds"/> 为 0 时在当前帧末销毁。
        /// </summary>
        public static bool SafeDestroy(UnityEngine.Object obj, float delaySeconds = 0f)
        {
            if (!IsAlive(obj)) return false;
            MainThread.AssertMainThread("UnityObject.SafeDestroy");
            try
            {
                return UnityCall.DestroyWithDelay(obj, delaySeconds);
            }
            catch (Exception e)
            {
                SdkLog.Error("UNITY", "Destroy 失败: " + e.Message);
                return false;
            }
        }

        /// <summary>立即销毁(仅建议在非播放状态或明确需要时使用)。</summary>
        public static bool SafeDestroyImmediate(UnityEngine.Object obj)
        {
            if (!IsAlive(obj)) return false;
            MainThread.AssertMainThread("UnityObject.SafeDestroyImmediate");
            try
            {
                return UnityCall.DestroyImmediate(obj);
            }
            catch (Exception e)
            {
                SdkLog.Error("UNITY", "DestroyImmediate 失败: " + e.Message);
                return false;
            }
        }

        /// <summary>跨场景保留。</summary>
        public static bool DontDestroyOnLoad(UnityEngine.Object obj)
        {
            if (!IsAlive(obj)) return false;
            try
            {
                return UnityCall.DontDestroyOnLoad(obj);
            }
            catch (Exception e)
            {
                SdkLog.Warn("UNITY", "DontDestroyOnLoad 失败: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 安全取组件(不需要先判 gameObject 存活)。
        /// </summary>
        public static T GetComponent<T>(UnityEngine.Object obj) where T : class
        {
            try
            {
                if (!IsAlive(obj)) return null;
                var go = obj as GameObject;
                if (go != null) return UnityCall.GetComponent(go, typeof(T)) as T;
                var comp = obj as Component;
                if (comp != null) return UnityCall.GetComponent(comp, typeof(T)) as T;
                return null;
            }
            catch { return null; }
        }

        /// <summary>安全取组件, 返回是否成功。</summary>
        public static bool TryGetComponent<T>(UnityEngine.Object obj, out T component) where T : class
        {
            component = GetComponent<T>(obj);
            return component != null;
        }

        /// <summary>安全取 GameObject。</summary>
        public static GameObject GetGameObject(UnityEngine.Object obj)
        {
            try
            {
                if (!IsAlive(obj)) return null;
                var go = obj as GameObject;
                if (go != null) return go;
                var comp = obj as Component;
                return comp != null ? UnityCall.GameObjectOf(comp) : null;
            }
            catch { return null; }
        }

        /// <summary>安全取 Transform。</summary>
        public static Transform GetTransform(UnityEngine.Object obj)
        {
            try
            {
                if (!IsAlive(obj)) return null;
                var t = obj as Transform;
                if (t != null) return t;
                return UnityCall.TransformOf(GetGameObject(obj));
            }
            catch { return null; }
        }

        /// <summary>
        /// 查找场景中所有 T 类型对象(包含未激活对象)。
        ///
        /// 注意: 这是 <b>按需</b> API(内部用 Resources.FindObjectsOfTypeAll),
        /// 开销较大, 禁止每帧调用。SDK 内部只在缓存失效(场景切换)后使用。
        /// 已自动剔除资源(Project 资产)对象, 只返回场景中的实例。
        /// </summary>
        public static List<T> FindAll<T>(bool includeInactive = true) where T : UnityEngine.Object
        {
            var result = new List<T>();
            try
            {
                MainThread.AssertMainThread("UnityObject.FindAll<" + typeof(T).Name + ">");

                var all = UnityCall.FindObjectsOfTypeAll(typeof(T));
                if (all == null) return result;

                for (int i = 0; i < all.Length; i++)
                {
                    var obj = all[i] as T;
                    if (obj == null) continue;
                    if (!IsSceneObject(obj)) continue;
                    if (!includeInactive && !IsActiveInHierarchy(obj)) continue;
                    result.Add(obj);
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("UNITY", "FindAll<" + typeof(T).Name + "> 失败: " + e.Message);
            }
            return result;
        }

        /// <summary>查找场景中第一个 T 类型对象。</summary>
        public static T FindAny<T>(bool includeInactive = true) where T : UnityEngine.Object
        {
            try
            {
                MainThread.AssertMainThread("UnityObject.FindAny<" + typeof(T).Name + ">");

                var all = UnityCall.FindObjectsOfTypeAll(typeof(T));
                if (all == null) return null;

                for (int i = 0; i < all.Length; i++)
                {
                    var obj = all[i] as T;
                    if (obj == null) continue;
                    if (!IsSceneObject(obj)) continue;
                    if (!includeInactive && !IsActiveInHierarchy(obj)) continue;
                    return obj;
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("UNITY", "FindAny<" + typeof(T).Name + "> 失败: " + e.Message);
            }
            return null;
        }

        /// <summary>对象是否属于某个有效场景(排除 Project 资产)。</summary>
        public static bool IsSceneObject(UnityEngine.Object obj)
        {
            try
            {
                var go = GetGameObject(obj);
                if (go == null) return true; // 非 GameObject/Component 类型不做过滤
                return UnityCall.SceneIsValid(go);
            }
            catch { return false; }
        }

        /// <summary>对象在层级中是否激活。</summary>
        public static bool IsActiveInHierarchy(UnityEngine.Object obj)
        {
            try
            {
                var go = GetGameObject(obj);
                return go != null && UnityCall.ActiveInHierarchy(go);
            }
            catch { return false; }
        }

        /// <summary>层级路径(诊断用), 形如 <c>/Root/Child/Leaf</c>。</summary>
        public static string GetHierarchyPath(UnityEngine.Object obj)
        {
            try
            {
                Transform t = GetTransform(obj);
                if (t == null) return "(无 Transform)";

                var parts = new List<string>(8);
                Transform cursor = t;
                int guard = 0;
                while (cursor != null && guard++ < 128)
                {
                    parts.Add(UnityCall.Name(cursor));
                    cursor = UnityCall.Parent(cursor);
                }
                parts.Reverse();
                return "/" + string.Join("/", parts.ToArray());
            }
            catch { return "(读取失败)"; }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// GameObject 创建 / 查找 / 组件操作的安全封装。
    /// 全部空安全, 全部需要在主线程调用(非主线程会记录一次警告)。
    /// </summary>
    public static class GameObjectUtil
    {
        /// <summary>按路径查找(等价 GameObject.Find, 只能找到激活对象)。</summary>
        public static GameObject Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Guard("Find");
            return UnityCall.Find(path);
        }

        /// <summary>按标签查找(激活对象)。</summary>
        public static GameObject FindWithTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            Guard("FindWithTag");
            return UnityCall.FindWithTag(tag);
        }

        /// <summary>按标签查找全部(激活对象)。</summary>
        public static GameObject[] FindAllWithTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return new GameObject[0];
            Guard("FindAllWithTag");
            return UnityCall.FindGameObjectsWithTag(tag);
        }

        /// <summary>创建 GameObject。</summary>
        public static GameObject Create(string name, params Type[] components)
        {
            Guard("Create");
            try
            {
                var go = components != null && components.Length > 0
                    ? new GameObject(name, components)
                    : new GameObject(string.IsNullOrEmpty(name) ? "CesiumObject" : name);
                return go;
            }
            catch (Exception e)
            {
                SdkLog.Error("UNITY", "创建 GameObject 失败: " + e.Message);
                return null;
            }
        }

        /// <summary>创建带父节点的 GameObject(局部坐标归零)。</summary>
        public static GameObject Create(string name, Transform parent, bool worldPositionStays = false)
        {
            var go = Create(name);
            if (go == null) return null;
            try
            {
                if (parent != null) UnityCall.SetParent(UnityCall.TransformOf(go), parent, worldPositionStays);
                UnityCall.SetLocalPosition(UnityCall.TransformOf(go), Vector3.zero);
                UnityCall.SetLocalRotation(UnityCall.TransformOf(go), Quaternion.identity);
            }
            catch { }
            return go;
        }

        /// <summary>创建 Camera 对象并返回其 Camera 组件(失败返回 null)。</summary>
        public static Camera CreateCameraObject(string name = "CesiumCamera", Transform parent = null)
        {
            var go = Create(name, parent);
            if (go == null) return null;
            var cam = UnityCall.AddComponent(go, typeof(Camera)) as Camera;
            if (cam != null) return cam;
            SdkLog.Error("UNITY", "创建 Camera 失败: AddComponent 不可用");
            UnityObject.SafeDestroy(go);
            return null;
        }

        /// <summary>取组件(不存在返回 null)。</summary>
        public static T GetComponent<T>(GameObject go) where T : class
        {
            return go == null ? null : UnityCall.GetComponent(go, typeof(T)) as T;
        }

        /// <summary>取组件, 不存在则添加。</summary>
        public static T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            if (go == null) return null;
            Guard("GetOrAddComponent");
            try
            {
                var existing = UnityCall.GetComponent(go, typeof(T)) as T;
                if (existing != null) return existing;
                return UnityCall.AddComponent(go, typeof(T)) as T;
            }
            catch (Exception e)
            {
                SdkLog.Error("UNITY", "AddComponent<" + typeof(T).Name + "> 失败: " + e.Message);
                return null;
            }
        }

        /// <summary>按类型添加组件(用于运行时才知道类型的情形)。</summary>
        public static Component AddComponent(GameObject go, Type type)
        {
            if (go == null || type == null) return null;
            Guard("AddComponent");
            var added = UnityCall.AddComponent(go, type);
            if (added != null) return added;
            SdkLog.Error("UNITY", "AddComponent(" + type.FullName + ") 失败");
            return null;
        }

        /// <summary>取子节点中的组件。</summary>
        public static T GetComponentInChildren<T>(GameObject go, bool includeInactive = false) where T : class
        {
            return go == null ? null : UnityCall.GetComponentInChildren(go, typeof(T), includeInactive) as T;
        }

        /// <summary>取父节点中的组件。</summary>
        public static T GetComponentInParent<T>(GameObject go) where T : class
        {
            return go == null ? null : UnityCall.GetComponentInParent(go, typeof(T)) as T;
        }

        /// <summary>取全部子组件。</summary>
        public static List<T> GetComponentsInChildren<T>(GameObject go, bool includeInactive = false) where T : class
        {
            var result = new List<T>();
            try
            {
                if (go == null) return result;
                var all = UnityCall.GetComponentsInChildren(go, typeof(T), includeInactive);
                if (all == null) return result;
                for (int i = 0; i < all.Length; i++)
                {
                    var item = all[i] as T;
                    if (item != null) result.Add(item);
                }
            }
            catch { }
            return result;
        }

        /// <summary>设置激活状态。</summary>
        public static bool SetActive(GameObject go, bool active)
        {
            if (go == null) return false;
            Guard("SetActive");
            return UnityCall.SetActive(go, active);
        }

        /// <summary>是否激活。</summary>
        public static bool IsActive(GameObject go)
        {
            return go != null && UnityCall.ActiveInHierarchy(go);
        }

        /// <summary>设置层级(Layer)。</summary>
        public static bool SetLayer(GameObject go, int layer)
        {
            if (go == null) return false;
            Guard("SetLayer");
            return UnityCall.SetLayer(go, layer);
        }

        /// <summary>设置 tag(不存在时会抛异常, 已被吞掉)。</summary>
        public static bool SetTag(GameObject go, string tag)
        {
            if (go == null || string.IsNullOrEmpty(tag)) return false;
            Guard("SetTag");
            return UnityCall.SetTag(go, tag);
        }

        /// <summary>是否有指定 tag。</summary>
        public static bool HasTag(GameObject go, string tag)
        {
            if (go == null || string.IsNullOrEmpty(tag)) return false;
            return UnityCall.CompareTag(go, tag);
        }

        /// <summary>查找单个组件的场景实例(按需, 非每帧 API)。</summary>
        public static T FindObjectOfType<T>(bool includeInactive = true) where T : UnityEngine.Object
        {
            return UnityObject.FindAny<T>(includeInactive);
        }

        /// <summary>查找全部组件实例(按需, 非每帧 API)。</summary>
        public static List<T> FindObjectsOfType<T>(bool includeInactive = true) where T : UnityEngine.Object
        {
            return UnityObject.FindAll<T>(includeInactive);
        }

        internal static void Guard(string what)
        {
            MainThread.AssertMainThread("GameObjectUtil." + what);
        }
    }
}

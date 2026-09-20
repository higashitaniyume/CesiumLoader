using System;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// Transform 读写封装(全部空安全)。
    ///
    /// 线程约定: Transform 属于 Unity 对象, 必须在主线程访问。本类会在非主线程时
    /// 记录一次警告(不抛异常), 所以请从 <c>OnUpdate</c> / <c>MainThread.Run</c> 里调用。
    /// </summary>
    public static class TransformService
    {
        // ---------- 世界坐标 ----------

        /// <summary>读取世界坐标(Transform 为 null 时返回 Vector3.zero)。</summary>
        public static Vector3 GetPosition(Transform t)
        {
            if (t == null) return Vector3.zero;
            Guard("GetPosition");
            return UnityCall.Position(t);
        }

        /// <summary>设置世界坐标。</summary>
        public static bool SetPosition(Transform t, Vector3 position)
        {
            if (t == null) return false;
            Guard("SetPosition");
            return UnityCall.SetPosition(t, position);
        }

        /// <summary>按分量设置世界坐标。</summary>
        public static bool SetPosition(Transform t, float x, float y, float z)
        {
            return SetPosition(t, new Vector3(x, y, z));
        }

        /// <summary>读取世界旋转。</summary>
        public static Quaternion GetRotation(Transform t)
        {
            if (t == null) return Quaternion.identity;
            Guard("GetRotation");
            return UnityCall.Rotation(t);
        }

        /// <summary>设置世界旋转。</summary>
        public static bool SetRotation(Transform t, Quaternion rotation)
        {
            if (t == null) return false;
            Guard("SetRotation");
            return UnityCall.SetRotation(t, rotation);
        }

        /// <summary>读取世界欧拉角。</summary>
        public static Vector3 GetEulerAngles(Transform t)
        {
            if (t == null) return Vector3.zero;
            Guard("GetEulerAngles");
            return UnityCall.EulerAngles(t);
        }

        /// <summary>设置世界欧拉角。</summary>
        public static bool SetEulerAngles(Transform t, Vector3 euler)
        {
            if (t == null) return false;
            Guard("SetEulerAngles");
            return UnityCall.SetEulerAngles(t, euler);
        }

        /// <summary>设置世界欧拉角(按分量)。</summary>
        public static bool SetEulerAngles(Transform t, float x, float y, float z)
        {
            return SetEulerAngles(t, new Vector3(x, y, z));
        }

        /// <summary>前方向量。</summary>
        public static Vector3 GetForward(Transform t)
        {
            if (t == null) return Vector3.forward;
            Guard("GetForward");
            return UnityCall.Forward(t);
        }

        /// <summary>右方向量。</summary>
        public static Vector3 GetRight(Transform t)
        {
            if (t == null) return Vector3.right;
            Guard("GetRight");
            return UnityCall.Right(t);
        }

        /// <summary>上方向量。</summary>
        public static Vector3 GetUp(Transform t)
        {
            if (t == null) return Vector3.up;
            Guard("GetUp");
            return UnityCall.Up(t);
        }

        // ---------- 局部坐标 ----------

        /// <summary>读取局部坐标。</summary>
        public static Vector3 GetLocalPosition(Transform t)
        {
            if (t == null) return Vector3.zero;
            Guard("GetLocalPosition");
            return UnityCall.LocalPosition(t);
        }

        /// <summary>设置局部坐标。</summary>
        public static bool SetLocalPosition(Transform t, Vector3 position)
        {
            if (t == null) return false;
            Guard("SetLocalPosition");
            return UnityCall.SetLocalPosition(t, position);
        }

        /// <summary>读取局部旋转。</summary>
        public static Quaternion GetLocalRotation(Transform t)
        {
            if (t == null) return Quaternion.identity;
            Guard("GetLocalRotation");
            return UnityCall.LocalRotation(t);
        }

        /// <summary>设置局部旋转。</summary>
        public static bool SetLocalRotation(Transform t, Quaternion rotation)
        {
            if (t == null) return false;
            Guard("SetLocalRotation");
            return UnityCall.SetLocalRotation(t, rotation);
        }

        /// <summary>读取局部欧拉角。</summary>
        public static Vector3 GetLocalEulerAngles(Transform t)
        {
            if (t == null) return Vector3.zero;
            Guard("GetLocalEulerAngles");
            return UnityCall.LocalEulerAngles(t);
        }

        /// <summary>设置局部欧拉角。</summary>
        public static bool SetLocalEulerAngles(Transform t, Vector3 euler)
        {
            if (t == null) return false;
            Guard("SetLocalEulerAngles");
            return UnityCall.SetLocalEulerAngles(t, euler);
        }

        /// <summary>读取局部缩放。</summary>
        public static Vector3 GetLocalScale(Transform t)
        {
            if (t == null) return Vector3.one;
            Guard("GetLocalScale");
            return UnityCall.LocalScale(t);
        }

        /// <summary>设置局部缩放。</summary>
        public static bool SetLocalScale(Transform t, Vector3 scale)
        {
            if (t == null) return false;
            Guard("SetLocalScale");
            return UnityCall.SetLocalScale(t, scale);
        }

        // ---------- 位移 / 旋转 ----------

        /// <summary>按世界方向平移。</summary>
        public static bool Translate(Transform t, Vector3 translation, Space space = Space.World)
        {
            if (t == null) return false;
            Guard("Translate");
            return UnityCall.Translate(t, translation, space);
        }

        /// <summary>按分量平移。</summary>
        public static bool Translate(Transform t, float x, float y, float z, Space space = Space.World)
        {
            return Translate(t, new Vector3(x, y, z), space);
        }

        /// <summary>按欧拉角旋转。</summary>
        public static bool Rotate(Transform t, Vector3 euler, Space space = Space.Self)
        {
            if (t == null) return false;
            Guard("Rotate");
            return UnityCall.RotateEuler(t, euler, space);
        }

        /// <summary>按分量旋转。</summary>
        public static bool Rotate(Transform t, float x, float y, float z, Space space = Space.Self)
        {
            return Rotate(t, new Vector3(x, y, z), space);
        }

        /// <summary>绕轴旋转指定角度。</summary>
        public static bool Rotate(Transform t, Vector3 axis, float angle, Space space = Space.Self)
        {
            if (t == null) return false;
            Guard("Rotate(axis)");
            return UnityCall.RotateAxis(t, axis, angle, space);
        }

        /// <summary>朝向世界坐标点。</summary>
        public static bool LookAt(Transform t, Vector3 worldPosition, Vector3 worldUp)
        {
            if (t == null) return false;
            Guard("LookAt");
            return UnityCall.LookAtPosition(t, worldPosition, worldUp);
        }

        /// <summary>朝向世界坐标点(默认以 Vector3.up 为上)。</summary>
        public static bool LookAt(Transform t, Vector3 worldPosition)
        {
            return LookAt(t, worldPosition, Vector3.up);
        }

        /// <summary>朝向另一个 Transform。</summary>
        public static bool LookAt(Transform t, Transform target)
        {
            if (t == null || target == null) return false;
            Guard("LookAt(Transform)");
            return UnityCall.LookAtTransform(t, target);
        }

        // ---------- 层级 ----------

        /// <summary>设置父节点。</summary>
        public static bool SetParent(Transform t, Transform parent, bool worldPositionStays = true)
        {
            if (t == null) return false;
            Guard("SetParent");
            return UnityCall.SetParent(t, parent, worldPositionStays);
        }

        /// <summary>父节点(顶层返回 null)。</summary>
        public static Transform GetParent(Transform t)
        {
            if (t == null) return null;
            return UnityCall.Parent(t);
        }

        /// <summary>子节点数量。</summary>
        public static int GetChildCount(Transform t)
        {
            if (t == null) return 0;
            return UnityCall.ChildCount(t);
        }

        /// <summary>按索引取子节点。</summary>
        public static Transform GetChild(Transform t, int index)
        {
            if (t == null) return null;
            if (index < 0 || index >= UnityCall.ChildCount(t)) return null;
            return UnityCall.GetChild(t, index);
        }

        /// <summary>按名字查找子节点。</summary>
        public static Transform Find(Transform t, string name)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            return UnityCall.FindChild(t, name);
        }

        /// <summary>世界坐标转局部坐标; 失败返回 Vector3.zero。</summary>
        public static Vector3 InverseTransformPoint(Transform t, Vector3 worldPosition)
        {
            if (t == null) return Vector3.zero;
            Guard("InverseTransformPoint");
            return UnityCall.InverseTransformPoint(t, worldPosition);
        }

        /// <summary>局部坐标转世界坐标。</summary>
        public static Vector3 TransformPoint(Transform t, Vector3 localPosition)
        {
            if (t == null) return Vector3.zero;
            Guard("TransformPoint");
            return UnityCall.TransformPoint(t, localPosition);
        }

        internal static void Guard(string what)
        {
            MainThread.AssertMainThread("TransformService." + what);
        }
    }
}

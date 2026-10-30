using System;
using System.Reflection;

namespace LanguageUnlockMod
{
    /// <summary>
    /// protobuf 生成类型的属性都是 private set，配置表修改统一走这里。
    /// </summary>
    internal static class Reflective
    {
        internal static bool Set(object target, string propertyName, object value)
        {
            if (target == null || string.IsNullOrEmpty(propertyName)) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static object Get(object target, string propertyName)
        {
            if (target == null || string.IsNullOrEmpty(propertyName)) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return property == null ? null : property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        internal static string GetString(object target, string propertyName)
        {
            return Get(target, propertyName) as string;
        }
    }
}

#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace CharacterProbeMod
{
    public static class ProbeReflection
    {
        private sealed class Accessor
        {
            public MethodInfo Getter;
            public FieldInfo Field;
            public object Read(object obj) { return Getter != null ? Getter.Invoke(obj, null) : Field.GetValue(obj); }
        }
        private static readonly Dictionary<string, Accessor> Cache = new Dictionary<string, Accessor>();
        public static object Read(object obj, string name)
        {
            if (obj == null) return null;
            var originalType = obj.GetType();
            string key = originalType.FullName + ":" + name;
            if (Cache.TryGetValue(key, out var cached)) return cached.Read(obj);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var type = originalType; type != null; type = type.BaseType)
            {
                // AOT builds can preserve get_* methods while dropping the PropertyInfo record.
                var getter = type.GetMethod("get_" + name, flags);
                if (getter == null)
                {
                    var property = type.GetProperty(name, flags);
                    if (property != null) getter = property.GetGetMethod(true);
                }
                if (getter != null)
                {
                    var accessor = new Accessor { Getter = getter };
                    Cache[key] = accessor;
                    return accessor.Read(obj);
                }
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    var accessor = new Accessor { Field = field };
                    Cache[key] = accessor;
                    return accessor.Read(obj);
                }
            }
            // MissingMemberException(type, member) itself is unavailable in the game's BCL.
            throw new Exception("Probe member unavailable: " + originalType.FullName + "." + name);
        }
    }
}

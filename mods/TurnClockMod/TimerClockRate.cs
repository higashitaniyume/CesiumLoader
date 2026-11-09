using System;
using System.Reflection;

namespace TurnClockMod
{
    /// <summary>UnityTimer can use either scaled game time or realtime, per instance.</summary>
    internal sealed class TimerClockRate
    {
        private readonly PropertyInfo _property;
        private readonly FieldInfo _field;

        internal TimerClockRate(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperty("usesRealTime", flags);
            if (property != null && property.PropertyType == typeof(bool) && property.CanRead && property.GetIndexParameters().Length == 0)
                _property = property;
            foreach (string name in new[] { "usesRealTime", "_usesRealTime", "<usesRealTime>k__BackingField" })
            {
                var field = type.GetField(name, flags);
                if (field == null || field.FieldType != typeof(bool)) continue;
                _field = field;
                break;
            }
        }

        internal double EffectiveSpeed(object timer, double nativeSpeed, double unityScale)
        {
            if (double.IsNaN(nativeSpeed) || double.IsInfinity(nativeSpeed) || nativeSpeed < 1) nativeSpeed = 1;
            // Unknown layouts retain native-only behavior. Do not guess that an
            // unrecognized timer uses scaled time, or slow its realtime countdown.
            try
            {
                object value = _property != null ? _property.GetValue(timer, null) : _field?.GetValue(timer);
                if (!(value is bool) || (bool)value) return nativeSpeed;
                if (double.IsNaN(unityScale) || double.IsInfinity(unityScale) || unityScale < 0) return nativeSpeed;
                return Math.Max(1, nativeSpeed * unityScale);
            }
            catch { return nativeSpeed; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using UnityEngine;

namespace CesiumLoader.SDK.UserInterface
{
    /// <summary>文本对齐方式(对应 UnityEngine.TextAnchor, 不引用 uGUI 程序集)。</summary>
    public enum WidgetAnchor
    {
        TopLeft,
        Top,
        TopRight,
        MiddleLeft,
        Center,
        MiddleRight,
        BottomLeft,
        Bottom,
        BottomRight,
        /// <summary>文本没有"拉伸"语义, 等同于 <see cref="Center"/>。</summary>
        Stretch
    }

    /// <summary>
    /// 一个已创建的控件句柄。
    ///
    /// 为什么不是直接返回 <c>Text</c> / <c>Slider</c> / <c>Image</c>:
    /// SDK 编译期只引用 UnityEngine.CoreModule, 拿不到 uGUI 类型, 所以底层组件以
    /// <see cref="Component"/> 暴露, 常用操作走 <see cref="SetText"/> / <see cref="SetValue"/> 等方法。
    /// </summary>
    public sealed class WidgetHandle
    {
        internal WidgetHandle(RectTransform rect, Component component)
        {
            Rect = rect;
            Component = component;
        }

        /// <summary>控件根节点(一定在 CoreModule 中, 可直接用于布局)。</summary>
        public RectTransform Rect { get; private set; }

        /// <summary>底层 uGUI 组件(Text / Image / Slider / Canvas), 可能为 null。</summary>
        public Component Component { get; private set; }

        /// <summary>控件根 GameObject(可能为 null)。</summary>
        public GameObject GameObject
        {
            get
            {
                var rect = Rect;
                if (ReferenceEquals(rect, null)) return null;
                return GetGameObjectCore(rect);
            }
        }

        // 单独成方法: Component.gameObject 是 ECall, 放在 getter 里会让整个 getter 在
        // 没有 Unity 运行时的宿主(离线单测)JIT 失败, 分支判断也救不了。
        private static GameObject GetGameObjectCore(RectTransform rect)
        {
            try { return rect.gameObject; }
            catch { return null; }
        }

        /// <summary>控件是否仍然存在(场景切换或销毁后为 false)。</summary>
        public bool IsAlive
        {
            get { return !ReferenceEquals(Rect, null) && UnityObject.IsAlive(Rect); }
        }

        /// <summary>设置文本(仅对 Text 控件有效)。</summary>
        public bool SetText(string text)
        {
            return WidgetService.SetMember(Component, "text", text ?? string.Empty);
        }

        /// <summary>设置数值(仅对 Slider 控件有效, 超出范围会被 Slider 自己夹紧)。</summary>
        public bool SetValue(float value)
        {
            return WidgetService.SetMember(Component, "value", value);
        }

        /// <summary>设置颜色(Text / Image)。</summary>
        public bool SetColor(Color color)
        {
            return WidgetService.SetMember(Component, "color", color);
        }

        /// <summary>显示 / 隐藏。</summary>
        public bool SetActive(bool active)
        {
            if (ReferenceEquals(Rect, null)) return false;
            var go = GameObject;
            return MainThread.Run(() => GameObjectUtil.SetActive(go, active), false);
        }

        /// <summary>是否接收鼠标命中(默认 false, 避免 HUD 挡住游戏点击)。</summary>
        public bool SetRaycastTarget(bool enabled)
        {
            return WidgetService.SetMember(Component, "raycastTarget", enabled);
        }

        /// <summary>设置 sizeDelta(锚点已拉伸时表示相对父节点的增量)。</summary>
        public bool SetSize(float width, float height)
        {
            if (ReferenceEquals(Rect, null)) return false;
            return MainThread.Run(() =>
            {
                try { Rect.sizeDelta = new Vector2(width, height); return true; }
                catch (Exception e) { SdkLog.Error("UI", "WidgetHandle.SetSize 失败: " + e.Message); return false; }
            }, false);
        }

        /// <summary>设置 anchoredPosition(相对锚点)。</summary>
        public bool SetAnchoredPosition(float x, float y)
        {
            if (ReferenceEquals(Rect, null)) return false;
            return MainThread.Run(() =>
            {
                try { Rect.anchoredPosition = new Vector2(x, y); return true; }
                catch (Exception e) { SdkLog.Error("UI", "WidgetHandle.SetAnchoredPosition 失败: " + e.Message); return false; }
            }, false);
        }

        /// <summary>销毁控件(连同所有子节点)。</summary>
        public bool Destroy()
        {
            return WidgetService.Destroy(Rect);
        }
    }

    /// <summary>
    /// 控件作用域: 记录本 mod 创建的根节点, <see cref="Dispose"/> 时统一销毁。
    /// 配合 <c>ModContext.RegisterCleanup</c> 使用, 保证 mod 卸载后不残留 UI。
    /// </summary>
    public sealed class WidgetScope : IDisposable
    {
        private readonly object _lock = new object();
        private readonly List<RectTransform> _roots = new List<RectTransform>();
        private bool _disposed;

        internal WidgetScope(string name)
        {
            Name = string.IsNullOrEmpty(name) ? "widget-scope" : name;
        }

        /// <summary>作用域名(诊断用)。</summary>
        public string Name { get; private set; }

        /// <summary>是否已释放。</summary>
        public bool IsDisposed
        {
            get { lock (_lock) return _disposed; }
        }

        /// <summary>已登记的根节点数量。</summary>
        public int Count
        {
            get { lock (_lock) return _roots.Count; }
        }

        /// <summary>登记一个根节点; 作用域已释放时立即销毁它。</summary>
        public RectTransform Track(RectTransform rect)
        {
            if (ReferenceEquals(rect, null)) return null;

            bool destroyNow;
            lock (_lock)
            {
                destroyNow = _disposed;
                if (!destroyNow) _roots.Add(rect);
            }

            if (destroyNow) WidgetService.Destroy(rect);
            return rect;
        }

        /// <summary>登记一个控件句柄; 返回同一个句柄, 便于链式书写。</summary>
        public WidgetHandle Track(WidgetHandle handle)
        {
            if (handle != null) Track(handle.Rect);
            return handle;
        }

        /// <summary>销毁本作用域登记的全部根节点(子节点随之销毁)。</summary>
        public void Dispose()
        {
            RectTransform[] roots;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                roots = _roots.ToArray();
                _roots.Clear();
            }

            for (int i = roots.Length - 1; i >= 0; i--) WidgetService.Destroy(roots[i]);
        }
    }

    /// <summary>
    /// 最小 uGUI 控件封装: Canvas / Panel / Text / Slider / Badge / Anchor。
    ///
    /// 定位: 只提供"建节点 + 设常见属性", <b>不做</b>统计面板、展开折叠、拖拽、换行估算、
    /// 颜色映射等业务逻辑, 这些留在 mod 里。
    ///
    /// 为什么是反射: SDK 编译期只引用 UnityEngine.CoreModule,
    /// <c>Canvas</c> / <c>Text</c> / <c>Image</c> / <c>Slider</c> 位于
    /// UnityEngine.UIModule / UnityEngine.TextRenderingModule / UnityEngine.UI,
    /// 无法在编译期绑定。运行期按类型名解析, 解析不到时 <see cref="IsAvailable"/> 为 false,
    /// 所有创建方法返回 null(不会抛异常)。
    ///
    /// 线程: 创建/销毁/改属性会自动切到主线程, 因此可以直接在 RPC 回调里调用。
    /// </summary>
    public static class WidgetService
    {
        /// <summary>推荐给常驻面板的 sortingOrder 起点。</summary>
        public const int DefaultSortingOrder = 1000;

        private static readonly object _lock = new object();
        private static readonly List<RectTransform> _canvases = new List<RectTransform>();

        private static bool _typesResolved;
        private static bool _available;
        private static string _reason;

        private static Type _canvasType;
        private static Type _canvasScalerType;
        private static Type _graphicRaycasterType;
        private static Type _textType;
        private static Type _imageType;
        private static Type _sliderType;
        private static Type _eventSystemType;
        private static Type _inputModuleType;
        private static Type _fontType;
        private static Type _textAnchorType;
        private static Type _resourcesType;

        private static bool _fontAttempted;
        private static UnityEngine.Object _builtinFont;

        private static Sprite _roundedSprite;
        private static bool _roundedSpriteFailed;

        // =====================================================================
        // 可用性
        // =====================================================================

        /// <summary>当前运行环境是否具备 uGUI(缺任何一个必要类型都为 false)。</summary>
        public static bool IsAvailable
        {
            get { EnsureTypes(); return _available; }
        }

        /// <summary>不可用时的原因; 可用时为 null。</summary>
        public static string UnavailableReason
        {
            get { EnsureTypes(); return _reason; }
        }

        private static void EnsureTypes()
        {
            lock (_lock)
            {
                if (_typesResolved) return;

                _canvasType = RuntimeAssemblyService.FindType("UnityEngine.Canvas");
                _canvasScalerType = RuntimeAssemblyService.FindType("UnityEngine.UI.CanvasScaler");
                _graphicRaycasterType = RuntimeAssemblyService.FindType("UnityEngine.UI.GraphicRaycaster");
                _textType = RuntimeAssemblyService.FindType("UnityEngine.UI.Text");
                _imageType = RuntimeAssemblyService.FindType("UnityEngine.UI.Image");
                _sliderType = RuntimeAssemblyService.FindType("UnityEngine.UI.Slider");
                _eventSystemType = RuntimeAssemblyService.FindType("UnityEngine.EventSystems.EventSystem");
                _inputModuleType = RuntimeAssemblyService.FindType("UnityEngine.EventSystems.StandaloneInputModule");
                _fontType = RuntimeAssemblyService.FindType("UnityEngine.Font");
                _textAnchorType = RuntimeAssemblyService.FindType("UnityEngine.TextAnchor");
                _resourcesType = RuntimeAssemblyService.FindType("UnityEngine.Resources");

                var missing = new List<string>();
                if (_canvasType == null) missing.Add("UnityEngine.Canvas");
                if (_textType == null) missing.Add("UnityEngine.UI.Text");
                if (_imageType == null) missing.Add("UnityEngine.UI.Image");
                if (_sliderType == null) missing.Add("UnityEngine.UI.Slider");

                _available = missing.Count == 0;
                _reason = _available
                    ? null
                    : "缺少 uGUI 类型: " + string.Join(", ", missing.ToArray()) +
                      " (SDK 编译期只引用 CoreModule, 运行期按名解析)";

                // 不可用时不锁死, 允许场景切换后重新解析(类型缓存由 RuntimeAssemblyService 维护)。
                _typesResolved = _available;

                if (!_available) SdkLog.Debug("UI", _reason);
            }
        }

        // =====================================================================
        // 创建
        // =====================================================================

        /// <summary>
        /// 创建一个独立 Canvas(ScreenSpaceOverlay)。
        /// </summary>
        /// <param name="name">节点名。</param>
        /// <param name="sortingOrder">层级, 默认 1000(高于游戏常规 UI)。</param>
        /// <param name="persist">是否跨场景保留(默认 true)。</param>
        public static WidgetHandle Canvas(string name, int sortingOrder = DefaultSortingOrder, bool persist = true)
        {
            if (!IsAvailable) return null;
            return MainThread.Run(() => CreateCanvasCore(name, sortingOrder, persist), null);
        }

        /// <summary>创建一个纯色面板(可选圆角)。</summary>
        public static WidgetHandle Panel(RectTransform parent, Color color, bool rounded = false)
        {
            if (ReferenceEquals(parent, null) || !IsAvailable) return null;
            return MainThread.Run(() => CreateImageCore(parent, "Panel", color, rounded, false), null);
        }

        /// <summary>创建文本。默认不接收鼠标命中, 自动换行。</summary>
        public static WidgetHandle Text(RectTransform parent, string text, int size, Color color, WidgetAnchor anchor)
        {
            if (ReferenceEquals(parent, null) || !IsAvailable) return null;
            return MainThread.Run(() => CreateTextCore(parent, text, size, color, anchor), null);
        }

        /// <summary>创建滑条(背景 + 填充), 数值变化请自行读取 Slider 的 onValueChanged。</summary>
        public static WidgetHandle Slider(RectTransform parent, float min, float max, float value)
        {
            if (ReferenceEquals(parent, null) || !IsAvailable) return null;
            return MainThread.Run(() => CreateSliderCore(parent, min, max, value), null);
        }

        /// <summary>创建徽章(带底色的 Image, 可选圆角)。</summary>
        public static WidgetHandle Badge(RectTransform parent, Color tint, bool rounded = true)
        {
            if (ReferenceEquals(parent, null) || !IsAvailable) return null;
            return MainThread.Run(() => CreateImageCore(parent, "Badge", tint, rounded, false), null);
        }

        /// <summary>创建控件作用域(配合 <c>ctx.RegisterCleanup(scope.Dispose)</c>)。</summary>
        public static WidgetScope CreateScope(string name = null)
        {
            return new WidgetScope(name);
        }

        // =====================================================================
        // 布局
        // =====================================================================

        /// <summary>设置锚点与偏移(等价于同时写 anchorMin / anchorMax / offsetMin / offsetMax)。</summary>
        public static bool Anchor(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            if (ReferenceEquals(rt, null)) return false;
            return MainThread.Run(() =>
            {
                try
                {
                    rt.anchorMin = anchorMin;
                    rt.anchorMax = anchorMax;
                    rt.offsetMin = offsetMin;
                    rt.offsetMax = offsetMax;
                    return true;
                }
                catch (Exception e)
                {
                    SdkLog.Error("UI", "WidgetService.Anchor 失败: " + e.Message);
                    return false;
                }
            }, false);
        }

        /// <summary>铺满父节点。</summary>
        public static bool Stretch(RectTransform rt, float padding = 0f)
        {
            return Anchor(rt, Vector2.zero, Vector2.one,
                new Vector2(padding, padding), new Vector2(-padding, -padding));
        }

        /// <summary>
        /// 把锚点固定到父节点的某个角/边, 并设置尺寸。
        /// <see cref="WidgetAnchor.Stretch"/> 时 <paramref name="position"/> 表示四边内缩, 忽略尺寸。
        /// </summary>
        public static bool Place(RectTransform rt, WidgetAnchor corner, Vector2 position, Vector2 size)
        {
            if (ReferenceEquals(rt, null)) return false;

            Vector2 min, max;
            switch (corner)
            {
                case WidgetAnchor.TopLeft: min = max = new Vector2(0f, 1f); break;
                case WidgetAnchor.Top: min = max = new Vector2(0.5f, 1f); break;
                case WidgetAnchor.TopRight: min = max = new Vector2(1f, 1f); break;
                case WidgetAnchor.MiddleLeft: min = max = new Vector2(0f, 0.5f); break;
                case WidgetAnchor.MiddleRight: min = max = new Vector2(1f, 0.5f); break;
                case WidgetAnchor.BottomLeft: min = max = new Vector2(0f, 0f); break;
                case WidgetAnchor.Bottom: min = max = new Vector2(0.5f, 0f); break;
                case WidgetAnchor.BottomRight: min = max = new Vector2(1f, 0f); break;
                case WidgetAnchor.Stretch: min = Vector2.zero; max = Vector2.one; break;
                default: min = max = new Vector2(0.5f, 0.5f); break;
            }

            if (corner == WidgetAnchor.Stretch)
            {
                return Anchor(rt, Vector2.zero, Vector2.one, position, -position);
            }

            bool ok = Anchor(rt, min, max, Vector2.zero, Vector2.zero);
            if (!ok) return false;
            return MainThread.Run(() =>
            {
                try
                {
                    rt.anchoredPosition = position;
                    rt.sizeDelta = size;
                    return true;
                }
                catch (Exception e)
                {
                    SdkLog.Error("UI", "WidgetService.Place 失败: " + e.Message);
                    return false;
                }
            }, false);
        }

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <summary>销毁控件根节点(子节点随之销毁)。</summary>
        public static bool Destroy(RectTransform rt)
        {
            if (ReferenceEquals(rt, null)) return false;
            return MainThread.Run(() =>
            {
                try
                {
                    lock (_lock) { _canvases.Remove(rt); }
                    var go = rt.gameObject;
                    if (go == null) return false;
                    return UnityObject.SafeDestroy(go);
                }
                catch (Exception e)
                {
                    SdkLog.Error("UI", "WidgetService.Destroy 失败: " + e.Message);
                    return false;
                }
            }, false);
        }

        /// <summary>销毁所有由 <see cref="Canvas"/> 创建且未单独销毁的 Canvas。</summary>
        public static int DestroyAll()
        {
            RectTransform[] roots;
            lock (_lock)
            {
                roots = _canvases.ToArray();
                _canvases.Clear();
            }

            int destroyed = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                if (Destroy(roots[i])) destroyed++;
            }
            return destroyed;
        }

        /// <summary>
        /// 确保场景里有 EventSystem(Slider 等交互控件需要)。
        /// 已有则复用, 不会重复创建。
        /// </summary>
        public static bool EnsureEventSystem()
        {
            if (!IsAvailable || _eventSystemType == null) return false;
            return MainThread.Run(() =>
            {
                try
                {
                    var find = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", new Type[] { typeof(Type) });
                    if (find != null)
                    {
                        var existing = find.Invoke(null, new object[] { _eventSystemType });
                        if (existing != null) return true;
                    }

                    var go = GameObjectUtil.Create("CesiumEventSystem");
                    if (go == null) return false;
                    if (GameObjectUtil.AddComponent(go, _eventSystemType) == null)
                    {
                        UnityObject.SafeDestroy(go);
                        return false;
                    }
                    if (_inputModuleType != null) GameObjectUtil.AddComponent(go, _inputModuleType);
                    UnityObject.DontDestroyOnLoad(go);
                    return true;
                }
                catch (Exception e)
                {
                    SdkLog.Error("UI", "WidgetService.EnsureEventSystem 失败: " + e.Message);
                    return false;
                }
            }, false);
        }

        // =====================================================================
        // 反射属性读写
        // =====================================================================

        internal static bool SetMember(object target, string member, object value)
        {
            if (target == null) return false;
            return MainThread.Run(() => SetMemberCore(target, member, value), false);
        }

        private static bool SetMemberCore(object target, string member, object value)
        {
            try
            {
                var property = target.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    if (value != null && !property.PropertyType.IsInstanceOfType(value)) return false;
                    property.SetValue(target, value, null);
                    return true;
                }

                var field = target.GetType().GetField(member, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    if (value != null && !field.FieldType.IsInstanceOfType(value)) return false;
                    field.SetValue(target, value);
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                SdkLog.Debug("UI", "设置 " + target.GetType().Name + "." + member + " 失败: " + e.Message);
                return false;
            }
        }

        private static object EnumValue(Type enumType, string name)
        {
            if (enumType == null || string.IsNullOrEmpty(name)) return null;
            try { return Enum.Parse(enumType, name, false); }
            catch
            {
                try
                {
                    var names = Enum.GetNames(enumType);
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                            return Enum.Parse(enumType, names[i], false);
                    }
                }
                catch { }
                return null;
            }
        }

        // =====================================================================
        // 内部创建
        // =====================================================================

        private static WidgetHandle CreateCanvasCore(string name, int sortingOrder, bool persist)
        {
            var rt = CreateRect(string.IsNullOrEmpty(name) ? "CesiumCanvas" : name, null);
            if (ReferenceEquals(rt, null)) return null;

            var go = rt.gameObject;
            var canvas = GameObjectUtil.AddComponent(go, _canvasType);
            if (canvas == null)
            {
                UnityObject.SafeDestroy(go);
                return null;
            }

            SetMemberCore(canvas, "renderMode", EnumValue(GetMemberType(canvas, "renderMode"), "ScreenSpaceOverlay"));
            SetMemberCore(canvas, "sortingOrder", sortingOrder);

            if (_canvasScalerType != null)
            {
                var scaler = GameObjectUtil.AddComponent(go, _canvasScalerType);
                if (scaler != null)
                {
                    SetMemberCore(scaler, "uiScaleMode", EnumValue(GetMemberType(scaler, "uiScaleMode"), "ScaleWithScreenSize"));
                    SetMemberCore(scaler, "referenceResolution", new Vector2(1920f, 1080f));
                    SetMemberCore(scaler, "screenMatchMode", EnumValue(GetMemberType(scaler, "screenMatchMode"), "MatchWidthOrHeight"));
                    SetMemberCore(scaler, "matchWidthOrHeight", 0.5f);
                }
            }

            if (_graphicRaycasterType != null) GameObjectUtil.AddComponent(go, _graphicRaycasterType);
            if (persist) UnityObject.DontDestroyOnLoad(go);

            lock (_lock) { _canvases.Add(rt); }
            return new WidgetHandle(rt, canvas);
        }

        private static WidgetHandle CreateImageCore(RectTransform parent, string name, Color color, bool rounded, bool raycast)
        {
            var rt = CreateRect(name, parent);
            if (ReferenceEquals(rt, null)) return null;

            var image = GameObjectUtil.AddComponent(rt.gameObject, _imageType);
            if (image == null)
            {
                UnityObject.SafeDestroy(rt.gameObject);
                return null;
            }

            SetMemberCore(image, "color", color);
            SetMemberCore(image, "raycastTarget", raycast);

            if (rounded)
            {
                var sprite = GetRoundedSprite();
                if (sprite != null)
                {
                    SetMemberCore(image, "sprite", sprite);
                    SetMemberCore(image, "type", EnumValue(GetMemberType(image, "type"), "Sliced"));
                }
            }

            return new WidgetHandle(rt, image);
        }

        private static WidgetHandle CreateTextCore(RectTransform parent, string text, int size, Color color, WidgetAnchor anchor)
        {
            var rt = CreateRect("Text", parent);
            if (ReferenceEquals(rt, null)) return null;

            var label = GameObjectUtil.AddComponent(rt.gameObject, _textType);
            if (label == null)
            {
                UnityObject.SafeDestroy(rt.gameObject);
                return null;
            }

            var font = GetBuiltinFont();
            if (font != null) SetMemberCore(label, "font", font);

            SetMemberCore(label, "text", text ?? string.Empty);
            SetMemberCore(label, "fontSize", Math.Max(1, size));
            SetMemberCore(label, "color", color);
            SetMemberCore(label, "alignment", EnumValue(_textAnchorType, ToTextAnchorName(anchor)));
            SetMemberCore(label, "raycastTarget", false);
            SetMemberCore(label, "supportRichText", true);
            SetMemberCore(label, "horizontalOverflow", EnumValue(GetMemberType(label, "horizontalOverflow"), "Wrap"));
            SetMemberCore(label, "verticalOverflow", EnumValue(GetMemberType(label, "verticalOverflow"), "Overflow"));

            return new WidgetHandle(rt, label);
        }

        private static WidgetHandle CreateSliderCore(RectTransform parent, float min, float max, float value)
        {
            var rt = CreateRect("Slider", parent);
            if (ReferenceEquals(rt, null)) return null;

            var slider = GameObjectUtil.AddComponent(rt.gameObject, _sliderType);
            if (slider == null)
            {
                UnityObject.SafeDestroy(rt.gameObject);
                return null;
            }

            // 背景(同时作为拖拽命中目标)
            var background = CreateImageCore(rt, "Background", new Color(0.12f, 0.12f, 0.14f, 0.9f), true, true);
            if (background == null)
            {
                UnityObject.SafeDestroy(rt.gameObject);
                return null;
            }

            var fillArea = CreateRect("Fill Area", rt);
            if (ReferenceEquals(fillArea, null))
            {
                UnityObject.SafeDestroy(rt.gameObject);
                return null;
            }
            fillArea.anchorMin = Vector2.zero;
            fillArea.anchorMax = Vector2.one;
            fillArea.offsetMin = new Vector2(4f, 4f);
            fillArea.offsetMax = new Vector2(-4f, -4f);

            var fill = CreateRect("Fill", fillArea);
            if (ReferenceEquals(fill, null))
            {
                UnityObject.SafeDestroy(rt.gameObject);
                return null;
            }
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;

            var fillImage = GameObjectUtil.AddComponent(fill.gameObject, _imageType);
            if (fillImage != null)
            {
                SetMemberCore(fillImage, "color", new Color(0.35f, 0.65f, 1f, 1f));
                SetMemberCore(fillImage, "raycastTarget", false);
            }

            if (max < min) max = min;
            float clamped = Mathf.Clamp(value, min, max);

            SetMemberCore(slider, "fillRect", fill);
            SetMemberCore(slider, "targetGraphic", background.Component);
            SetMemberCore(slider, "direction", EnumValue(GetMemberType(slider, "direction"), "LeftToRight"));
            SetMemberCore(slider, "minValue", min);
            SetMemberCore(slider, "maxValue", max);
            SetMemberCore(slider, "wholeNumbers", false);
            SetMemberCore(slider, "value", clamped);

            return new WidgetHandle(rt, slider);
        }

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            var go = GameObjectUtil.Create(string.IsNullOrEmpty(name) ? "CesiumWidget" : name, typeof(RectTransform));
            if (go == null) return null;

            var rt = GameObjectUtil.GetComponent<RectTransform>(go);
            if (ReferenceEquals(rt, null))
            {
                UnityObject.SafeDestroy(go);
                return null;
            }

            if (!ReferenceEquals(parent, null))
            {
                rt.SetParent(parent, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }

            rt.localScale = Vector3.one;
            return rt;
        }

        private static Type GetMemberType(object target, string member)
        {
            if (target == null) return null;
            try
            {
                var property = target.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                return property != null ? property.PropertyType : null;
            }
            catch { return null; }
        }

        private static string ToTextAnchorName(WidgetAnchor anchor)
        {
            switch (anchor)
            {
                case WidgetAnchor.TopLeft: return "UpperLeft";
                case WidgetAnchor.Top: return "UpperCenter";
                case WidgetAnchor.TopRight: return "UpperRight";
                case WidgetAnchor.MiddleLeft: return "MiddleLeft";
                case WidgetAnchor.MiddleRight: return "MiddleRight";
                case WidgetAnchor.BottomLeft: return "LowerLeft";
                case WidgetAnchor.Bottom: return "LowerCenter";
                case WidgetAnchor.BottomRight: return "LowerRight";
                default: return "MiddleCenter";
            }
        }

        // =====================================================================
        // 字体 / 圆角贴图
        // =====================================================================

        private static UnityEngine.Object GetBuiltinFont()
        {
            if (_builtinFont != null) return _builtinFont;
            if (_fontAttempted) return null;
            _fontAttempted = true;

            // 1) 内置字体(Unity 2022 为 LegacyRuntime.ttf, 旧版为 Arial.ttf)
            try
            {
                var get = _resourcesType != null
                    ? _resourcesType.GetMethod("GetBuiltinResource", new Type[] { typeof(Type), typeof(string) })
                    : null;
                if (get != null)
                {
                    string[] paths = { "LegacyRuntime.ttf", "Arial.ttf" };
                    for (int i = 0; i < paths.Length; i++)
                    {
                        try
                        {
                            var font = get.Invoke(null, new object[] { _fontType, paths[i] }) as UnityEngine.Object;
                            if (font != null)
                            {
                                _builtinFont = font;
                                return font;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 2) 系统字体
            try
            {
                var create = _fontType != null
                    ? _fontType.GetMethod("CreateDynamicFontFromOSFont", new Type[] { typeof(string), typeof(int) })
                    : null;
                if (create != null)
                {
                    string[] names = { "Microsoft YaHei", "Arial", "SimHei" };
                    for (int i = 0; i < names.Length; i++)
                    {
                        try
                        {
                            var font = create.Invoke(null, new object[] { names[i], 16 }) as UnityEngine.Object;
                            if (font != null)
                            {
                                _builtinFont = font;
                                return font;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            SdkLog.Warn("UI", "未找到可用字体, Text 控件会显示为空; 可在创建后自行设置 Text.font。");
            return null;
        }

        private static Sprite GetRoundedSprite()
        {
            if (_roundedSprite != null) return _roundedSprite;
            if (_roundedSpriteFailed) return null;

            try
            {
                const int size = 32;
                const int radius = 10;

                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.hideFlags = HideFlags.HideAndDontSave;

                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = Math.Max(Math.Max(radius - x - 0.5f, x + 0.5f - (size - radius)), 0f);
                        float dy = Math.Max(Math.Max(radius - y - 0.5f, y + 0.5f - (size - radius)), 0f);
                        float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                        float alpha = Mathf.Clamp01(radius - distance + 0.5f);
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                    }
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);

                _roundedSprite = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, size, size),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect,
                    new Vector4(radius, radius, radius, radius));
                _roundedSprite.hideFlags = HideFlags.HideAndDontSave;
                return _roundedSprite;
            }
            catch (Exception e)
            {
                _roundedSpriteFailed = true;
                SdkLog.Warn("UI", "生成圆角贴图失败, 圆角将退化为直角: " + e.Message);
                return null;
            }
        }
    }
}

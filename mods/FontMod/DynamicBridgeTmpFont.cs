using System;
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using UnityEngine;

namespace FontMod
{
    /// <summary>
    /// 将旧版 DynamicFont 无缝桥接到已构建的高清 Dynamic SDF TMP_FontAsset：
    /// - 继承自 FairyGUI.BaseFont，内部代理 FairyGUI.TMPFont 执行字形度量与 SDF 绘制；
    /// - 保持 customOutline = true，并在 UpdateGraphics 中清零所有旧版 DynamicFont 本身不会使用的
    ///   SDF 专属参数（faceDilate / outlineSoftness / underlaySoftness / outline / shadowOffset），
    ///   彻底避免游戏 UI 遗留的 faceDilate=1.0 导致文字糊成色块，同时完整保留原生像素描边与阴影。
    /// </summary>
    internal sealed class DynamicBridgeTmpFont : BaseFont
    {
        private readonly BaseFont _inner;
        private readonly PropertyInfo _fontAssetProp;
        private TextFormat _lastFormat;

        internal static DynamicBridgeTmpFont Create(string fontName, object tmpFontAsset)
        {
            if (tmpFontAsset == null) return null;
            Type tmpFontType = typeof(BaseFont).Assembly.GetType("FairyGUI.TMPFont");
            if (tmpFontType == null) return null;

            BaseFont inner = Activator.CreateInstance(tmpFontType) as BaseFont;
            if (inner == null) return null;

            PropertyInfo prop = tmpFontType.GetProperty("fontAsset", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return null;

            inner.name = fontName;
            prop.SetValue(inner, tmpFontAsset, null);

            var bridge = new DynamicBridgeTmpFont(fontName, inner, prop);
            bridge.SyncFromInner();
            return bridge;
        }

        private DynamicBridgeTmpFont(string fontName, BaseFont inner, PropertyInfo fontAssetProp)
        {
            this.name = fontName;
            this._inner = inner;
            this._fontAssetProp = fontAssetProp;
            this.canTint = true;
            this.keepCrisp = true;
            this.customOutline = true;
        }

        internal void SetTmpFontAsset(object tmpFontAsset)
        {
            if (_inner == null || _fontAssetProp == null || tmpFontAsset == null) return;
            _fontAssetProp.SetValue(_inner, tmpFontAsset, null);
            SyncFromInner();
            this.version++;
        }

        private void SyncFromInner()
        {
            this.mainTexture = _inner.mainTexture;
            this.shader = _inner.shader;
            this.canTint = _inner.canTint;
            this.keepCrisp = _inner.keepCrisp;
            this.customOutline = true;
        }

        public override void SetFormat(TextFormat format, float fontSizeScale)
        {
            _lastFormat = format;
            _inner.SetFormat(format, fontSizeScale);
        }

        public override void UpdateGraphics(NGraphics graphics)
        {
            if (_lastFormat != null)
            {
                float savedOutline = _lastFormat.outline;
                Vector2 savedShadow = _lastFormat.shadowOffset;
                float savedFaceDilate = _lastFormat.faceDilate;
                float savedOutlineSoftness = _lastFormat.outlineSoftness;
                float savedUnderlaySoftness = _lastFormat.underlaySoftness;
                _lastFormat.outline = 0f;
                _lastFormat.shadowOffset = Vector2.zero;
                _lastFormat.faceDilate = 0f;
                _lastFormat.outlineSoftness = 0f;
                _lastFormat.underlaySoftness = 0f;
                try
                {
                    _inner.UpdateGraphics(graphics);
                }
                finally
                {
                    _lastFormat.outline = savedOutline;
                    _lastFormat.shadowOffset = savedShadow;
                    _lastFormat.faceDilate = savedFaceDilate;
                    _lastFormat.outlineSoftness = savedOutlineSoftness;
                    _lastFormat.underlaySoftness = savedUnderlaySoftness;
                }
            }
            else
            {
                _inner.UpdateGraphics(graphics);
            }
        }

        public override void PrepareCharacters(string text)
        {
            _inner.PrepareCharacters(text);
        }

        public override bool GetGlyph(char ch, out float width, out float height, out float baseline)
        {
            return _inner.GetGlyph(ch, out width, out height, out baseline);
        }

        public override int DrawGlyph(
            float x,
            float y,
            List<Vector3> vertList,
            List<Vector2> uvList,
            List<Vector2> uv2List,
            List<Color32> colList)
        {
            return _inner.DrawGlyph(x, y, vertList, uvList, uv2List, colList);
        }

        public override int DrawLine(
            float x,
            float y,
            float width,
            int fontSize,
            int type,
            List<Vector3> vertList,
            List<Vector2> uvList,
            List<Vector2> uv2List,
            List<Color32> colList)
        {
            return _inner.DrawLine(x, y, width, fontSize, type, vertList, uvList, uv2List, colList);
        }

        public override bool HasCharacter(char ch)
        {
            return _inner.HasCharacter(ch);
        }

        public override int GetLineHeight(int size)
        {
            return _inner.GetLineHeight(size);
        }

        public override void Dispose()
        {
            try
            {
                if (_inner != null) _inner.Dispose();
            }
            catch
            {
            }
            this.mainTexture = null;
        }
    }
}

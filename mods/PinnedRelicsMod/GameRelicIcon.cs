using System;
using FairyGUI;
using Tools;
using UI;

namespace PinnedRelicsMod
{
    /// <summary>
    /// Uses the same quality component and TextureLoader path as BattlePlayerInfoWindow.
    /// The game owns the shared textures; disposing the loader releases its reference.
    /// </summary>
    internal sealed class GameRelicIcon : IDisposable
    {
        internal readonly GComponent View;
        private readonly UICom_Relic_Quality _quality;
        private readonly TextureLoader _picture;
        private float _lastSize;
        internal bool IsLoaded => _picture.texture != null && !_picture.texture.disposed;

        internal GameRelicIcon(int relicId)
        {
            var config = relicId.GetRelicInfoConfigure();
            if (config == null || string.IsNullOrEmpty(config.Icon))
                throw new InvalidOperationException("游戏没有筹码图标配置：" + relicId);

            View = new GComponent { name = "Relic" + relicId, touchable = false };
            try
            {
                _quality = UICom_Relic_Quality.CreateInstance();
                if (_quality == null || _quality.quality == null || _quality.width <= 0 || _quality.height <= 0)
                    throw new InvalidOperationException("游戏公共筹码品质组件尚未就绪");
                _quality.touchable = false;
                _quality.quality.selectedIndex = (int)config.RelicQualityType;
                View.AddChild(_quality);

                // new GLoader would use the base class external loader. TextureLoader
                // routes the configured UT_Relic_* key through the game's TextureManager.
                _picture = new TextureLoader
                {
                    touchable = false,
                    autoSize = false,
                    fill = FillType.Scale,
                    align = AlignType.Center,
                    verticalAlign = VertAlignType.Middle,
                    showErrorSign = false
                };
                View.AddChild(_picture);
                Layout(30);
                _picture.url = config.Icon;
            }
            catch
            {
                View.Dispose();
                if (_quality != null && !_quality.isDisposed) _quality.Dispose();
                throw;
            }
        }

        internal void Layout(float size)
        {
            if (size == _lastSize) return;
            _lastSize = size;
            View.SetSize(size, size);
            _quality.SetXY(0, 0);
            _quality.SetScale(size / _quality.width, size / _quality.height);
            // Preserve the original 248/300 symbol-to-base ratio and user offsets.
            float inset = size * 26 / 300;
            _picture.SetXY(inset, inset);
            _picture.SetSize(size - inset * 2, size - inset * 2);
        }

        public void Dispose()
        {
            if (!View.isDisposed) View.Dispose();
        }
    }
}

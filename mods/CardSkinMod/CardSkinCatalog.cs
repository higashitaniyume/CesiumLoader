using System;
using System.Collections.Generic;

namespace CardSkinMod
{
    /// <summary>将皮肤文件绑定到配置中的图片/视频素材名；无 Unity 依赖。</summary>
    public sealed class CardSkinCatalog
    {
        private readonly CardSkinModel _model;
        private readonly Dictionary<string, string> _assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguousNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _protectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CardSkinCatalog(CardSkinModel model)
        {
            _model = model;
            foreach (var entry in model.AssetKeyFiles)
                if (!IsProtectedAsset(entry.Key)) _assets[entry.Key] = entry.Value;
        }

        private readonly Dictionary<string, CardAnimation> _animations = new Dictionary<string, CardAnimation>(StringComparer.OrdinalIgnoreCase);
        public bool TryGetAnimation(string key, out CardAnimation animation)
        {
            animation = null;
            return !string.IsNullOrEmpty(key) && _animations.TryGetValue(key, out animation);
        }
        private readonly HashSet<string> _alternateAnimations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool IsAlternateAnimation(string key) => !string.IsNullOrEmpty(key) && _alternateAnimations.Contains(key);
        public void RegisterAlternate(int id, params string[] keys)
        {
            foreach (string key in keys)
                if (!string.IsNullOrEmpty(key) && (_model.TryGetExactAnimation(key, out var animation)
                    || _model.TryGetExactAnimation(id.ToString(), out animation)
                    || _model.TryGetExactAnimation("UT_Item_AltArt_" + id, out animation)
                    || _model.TryGetExactAnimation("UT_IAltArt_" + id, out animation)))
                { _animations[key] = animation; _alternateAnimations.Add(key); }
        }
        public IReadOnlyDictionary<string, string> AssetFiles => _assets;

        public void Register(int id, string name, string image, string imageSfw = null)
        {
            bool hasAnimation = _model.TryGetAnimationForCard(id, name, image, out var animation);
            if (hasAnimation && !string.IsNullOrEmpty(image)) _animations[image] = animation;
            if (!string.IsNullOrEmpty(imageSfw))
            {
                if (_model.TryGetExactAnimation(imageSfw, out var sfwAnimation)) _animations[imageSfw] = sfwAnimation;
                else if (hasAnimation) _animations[imageSfw] = animation;
            }
            if (!_model.TryGetSkinForCard(id, name, image, out var file)) return;
            Bind(image, file);
            if (!string.IsNullOrEmpty(imageSfw))
            {
                Bind(imageSfw, _model.TryGetExactSkinByAssetKey(imageSfw, out var sfwFile) ? sfwFile : file);
            }
            RegisterName(name, file);
        }

        public bool IsProtectedAsset(string key)
        {
            return !string.IsNullOrEmpty(key) && (_protectedKeys.Contains(key)
                || key.StartsWith("UT_Item_AltArt_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("UT_IAltArt_", StringComparison.OrdinalIgnoreCase));
        }

        public void ProtectAlternate(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                _protectedKeys.Add(key);
                _assets.Remove(key);
            }
        }

        public bool TryGetFile(string key, out string file)
        {
            file = null;
            return !string.IsNullOrEmpty(key) && !IsProtectedAsset(key) && _assets.TryGetValue(key, out file);
        }

        public bool TryGetFileByName(string name, out string file)
        {
            file = null;
            return !string.IsNullOrEmpty(name) && !_ambiguousNames.Contains(name) && _names.TryGetValue(name, out file);
        }

        private void Bind(string key, string file)
        {
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(file) && !IsProtectedAsset(key))
            {
                _assets.Remove(key);
                _assets[key] = file;
            }
        }

        private void RegisterName(string name, string file)
        {
            if (string.IsNullOrEmpty(name) || _ambiguousNames.Contains(name)) return;
            if (_names.TryGetValue(name, out var existing) && existing != file)
            {
                _names.Remove(name);
                _ambiguousNames.Add(name);
                return;
            }
            _names[name] = file;
        }
    }
}

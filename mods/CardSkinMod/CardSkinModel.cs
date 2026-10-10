using System;
using System.Collections.Generic;
using System.IO;

namespace CardSkinMod
{
    /// <summary>
    /// 皮肤方案元数据 (可选配置 skin.json)
    /// </summary>
    public sealed class SkinMetadata
    {
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// 卡面皮肤方案索引模型 (纯数据模型，无 Unity 依赖，可完全脱机单测)
    /// 负责按卡牌 ID、素材 Key (UT_HandCard_xxx) 或卡牌中文名索引皮肤图片，并处理回退。
    /// </summary>
    public sealed class CardSkinModel
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp"
        };

        private static readonly string[] KnownPrefixes = new[]
        {
            "UT_HandCard_",
            "UT_Destiny_",
            "UT_Event_",
            "UT_MapEvent_",
            "UT_Item_AltArt_",
            "UT_IAltArt_",
            "Card_",
            "card_"
        };

        private readonly Dictionary<int, string> _cardIdToPath = new Dictionary<int, string>();
        private readonly Dictionary<string, string> _cardNameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _assetKeyToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string SkinName { get; private set; } = "";
        public string DirectoryPath { get; private set; } = "";
        public SkinMetadata Metadata { get; private set; } = new SkinMetadata();
        public int FileCount => _assetKeyToPath.Count;

        /// <summary>
        /// 扫描并索引指定方案目录下的图片文件
        /// </summary>
        public bool LoadSkinDirectory(string skinDir, string skinName = null)
        {
            _cardIdToPath.Clear();
            _cardNameToPath.Clear();
            _assetKeyToPath.Clear();
            DirectoryPath = skinDir ?? "";
            SkinName = skinName ?? (string.IsNullOrEmpty(skinDir) ? "" : Path.GetFileName(skinDir));
            Metadata = new SkinMetadata { Name = SkinName };

            if (string.IsNullOrEmpty(skinDir) || !Directory.Exists(skinDir))
            {
                return false;
            }

            // 读取可选的 skin.json
            string metaFile = Path.Combine(skinDir, "skin.json");
            if (File.Exists(metaFile))
            {
                ParseMetadata(metaFile);
            }

            // 遍历扫描所有支持的图片文件
            var files = Directory.GetFiles(skinDir, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                string ext = Path.GetExtension(file);
                if (!SupportedExtensions.Contains(ext)) continue;

                string baseName = Path.GetFileNameWithoutExtension(file).Trim();
                if (string.IsNullOrEmpty(baseName)) continue;

                // 1. 记录完整 AssetKey 映射 (例如 UT_HandCard_10001, UT_Destiny_40001)
                _assetKeyToPath[baseName] = file;

                // 2. 尝试提取卡牌数字 ID
                if (int.TryParse(baseName, out int directId))
                {
                    _cardIdToPath[directId] = file;
                }
                else
                {
                    int parsedId = ExtractCardId(baseName, out bool isSfw);
                    if (parsedId > 0)
                    {
                        // 非 SFW 优先覆盖
                        if (!_cardIdToPath.ContainsKey(parsedId) || !isSfw)
                        {
                            _cardIdToPath[parsedId] = file;
                        }
                    }
                    else
                    {
                        _cardNameToPath[baseName] = file;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 查询该卡牌是否有自定义皮肤。若未指定则返回 false (触发回退)。
        /// </summary>
        public bool TryGetSkinFile(int cardId, string cardName, out string filePath)
        {
            // 优先按卡牌 ID 匹配
            if (cardId > 0)
            {
                if (_cardIdToPath.TryGetValue(cardId, out filePath))
                {
                    return true;
                }
                if (_assetKeyToPath.TryGetValue($"UT_HandCard_{cardId}", out filePath))
                {
                    return true;
                }
                if (_assetKeyToPath.TryGetValue($"UT_Destiny_{cardId}", out filePath))
                {
                    return true;
                }
                if (_assetKeyToPath.TryGetValue($"UT_Event_{cardId}", out filePath))
                {
                    return true;
                }
                if (_assetKeyToPath.TryGetValue($"UT_MapEvent_{cardId}", out filePath))
                {
                    return true;
                }
            }

            // 其次按卡牌中文名称匹配 (如 "加速卡")
            if (!string.IsNullOrEmpty(cardName) && _cardNameToPath.TryGetValue(cardName, out filePath))
            {
                return true;
            }

            filePath = null;
            return false;
        }

        /// <summary>
        /// 按卡牌身份查询，找不到时使用配置指定的实际素材名（支持共享卡图）。
        /// </summary>
        public bool TryGetSkinForCard(int cardId, string cardName, string assetKey, out string filePath)
        {
            // 天使模式明确提供的素材不能被同 ID 的普通素材覆盖。
            if (!string.IsNullOrEmpty(assetKey) && assetKey.EndsWith("_sfw", StringComparison.OrdinalIgnoreCase)
                && TryGetExactSkinByAssetKey(assetKey, out filePath)) return true;
            return TryGetSkinFile(cardId, cardName, out filePath)
                || TryGetSkinByAssetKey(assetKey, out filePath);
        }

        public bool TryGetExactSkinByAssetKey(string assetKey, out string filePath)
        {
            filePath = null;
            return !string.IsNullOrEmpty(assetKey) && _assetKeyToPath.TryGetValue(assetKey, out filePath);
        }

        /// <summary>
        /// 按 Unity AssetKey 查询自定义图片 (例如 "UT_HandCard_10001" 或 "UT_Destiny_40001")
        /// </summary>
        public bool TryGetSkinByAssetKey(string assetKey, out string filePath)
        {
            if (string.IsNullOrEmpty(assetKey))
            {
                filePath = null;
                return false;
            }

            if (_assetKeyToPath.TryGetValue(assetKey, out filePath))
            {
                return true;
            }

            int id = ExtractCardId(assetKey, out _);
            if (id > 0 && _cardIdToPath.TryGetValue(id, out filePath))
            {
                return true;
            }

            filePath = null;
            return false;
        }

        /// <summary>
        /// 获取所有已配置的卡牌 ID 列表
        /// </summary>
        public IEnumerable<int> ConfiguredCardIds => _cardIdToPath.Keys;

        /// <summary>
        /// 获取所有通过 AssetKey 索引的文件字典
        /// </summary>
        public IReadOnlyDictionary<string, string> AssetKeyFiles => _assetKeyToPath;

        public static int ExtractCardId(string name, out bool isSfw)
        {
            isSfw = false;
            if (string.IsNullOrEmpty(name)) return -1;

            string s = name;
            if (s.EndsWith("_sfw", StringComparison.OrdinalIgnoreCase))
            {
                isSfw = true;
                s = s.Substring(0, s.Length - 4);
            }

            foreach (var prefix in KnownPrefixes)
            {
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(prefix.Length);
                    break;
                }
            }

            if (int.TryParse(s, out int id))
            {
                return id;
            }

            return -1;
        }

        /// <summary>
        /// 简单解析 skin.json (不引入大型 JSON 库以保证通用性)
        /// </summary>
        private void ParseMetadata(string filePath)
        {
            try
            {
                string text = File.ReadAllText(filePath);
                Metadata.Name = ExtractJsonString(text, "name") ?? SkinName;
                Metadata.Author = ExtractJsonString(text, "author") ?? "";
                Metadata.Description = ExtractJsonString(text, "description") ?? "";
            }
            catch
            {
                // 解析异常保持默认
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int firstQuote = json.IndexOf('\"', colon + 1);
            if (firstQuote < 0) return null;
            int secondQuote = json.IndexOf('\"', firstQuote + 1);
            if (secondQuote < 0) return null;
            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }
    }
}

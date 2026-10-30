using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Core;

namespace LanguageUnlockMod
{
    /// <summary>
    /// mod 目录下 data\ 的语言数据：{en|ja|tc}.pack 与 {en|ja|tc}.xml。
    /// 只读、按语言缓存；文件缺失时对应语言视为不可用。
    /// </summary>
    internal sealed class LanguageData
    {
        private readonly string _root;
        private readonly Dictionary<LanguageType, LanguagePack> _packs = new Dictionary<LanguageType, LanguagePack>();
        private readonly Dictionary<LanguageType, string> _xmls = new Dictionary<LanguageType, string>();

        internal LanguageData(string modDirectory)
        {
            _root = Path.Combine(modDirectory ?? string.Empty, "data");
        }

        internal string Root { get { return _root; } }

        internal bool Has(LanguageType language)
        {
            string code = LanguageCodes.DataCode(language);
            if (code == null) return false;
            return File.Exists(PackPath(code)) && File.Exists(XmlPath(code));
        }

        /// <summary>读取（并缓存）正文数据包；失败返回 null。</summary>
        internal LanguagePack GetPack(LanguageType language, out string error)
        {
            error = null;
            string code = LanguageCodes.DataCode(language);
            if (code == null)
            {
                error = "简中不需要数据包";
                return null;
            }

            LanguagePack cached;
            if (_packs.TryGetValue(language, out cached)) return cached;

            string path = PackPath(code);
            if (!File.Exists(path))
            {
                error = "缺少数据包: " + path;
                return null;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e)
            {
                error = "读取数据包失败: " + e.Message;
                return null;
            }

            LanguagePack pack;
            string parseError;
            if (!LanguagePack.TryParse(bytes, out pack, out parseError))
            {
                error = parseError + " (" + path + ")";
                return null;
            }
            if (pack.Language != language)
            {
                error = "数据包语言不匹配: " + pack.Language + " != " + language;
                return null;
            }

            _packs[language] = pack;
            return pack;
        }

        /// <summary>读取（并缓存）FairyGUI 文案 XML；失败返回 null。</summary>
        internal string GetXml(LanguageType language, out string error)
        {
            error = null;
            string code = LanguageCodes.DataCode(language);
            if (code == null)
            {
                error = "简中不需要文案包";
                return null;
            }

            string cached;
            if (_xmls.TryGetValue(language, out cached)) return cached;

            string path = XmlPath(code);
            if (!File.Exists(path))
            {
                error = "缺少界面文案: " + path;
                return null;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int offset = 0;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
                string xml = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
                _xmls[language] = xml;
                return xml;
            }
            catch (Exception e)
            {
                error = "读取界面文案失败: " + e.Message;
                return null;
            }
        }

        private string PackPath(string code)
        {
            return Path.Combine(_root, code + ".pack");
        }

        private string XmlPath(string code)
        {
            return Path.Combine(_root, code + ".xml");
        }
    }
}


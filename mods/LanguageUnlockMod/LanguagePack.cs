using System;
using System.Collections.Generic;
using System.Text;
using Core;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 语言数据包：表名 → { STR Id → 该语言的文本 }。
    ///
    /// 文件格式（小端）：
    ///   "APL1" | int32 languageType | int32 tableCount
    ///   每张表：int32 名称字节数 + UTF-8 名称 | int32 entryCount
    ///   每个条目：int32 id | int32 文本字节数 + UTF-8 文本
    ///
    /// 只写与简中不同的条目；缺的条目运行期回退到配置表现有的简中文本，
    /// 所以国服以后新增的卡牌/道具不会变成空白。
    /// </summary>
    internal sealed class LanguagePack
    {
        private const int MaxTables = 512;
        private const int MaxEntriesPerTable = 200000;
        private const int MaxNameBytes = 256;
        private const int MaxTextBytes = 1024 * 1024;

        internal LanguageType Language { get; private set; }

        internal Dictionary<string, Dictionary<int, string>> Tables { get; private set; }

        internal static bool TryParse(byte[] data, out LanguagePack pack, out string error)
        {
            pack = null;
            error = null;

            try
            {
                if (data == null || data.Length < 12
                    || data[0] != (byte)'A' || data[1] != (byte)'P'
                    || data[2] != (byte)'L' || data[3] != (byte)'1')
                {
                    error = "不是 APL1 语言数据包";
                    return false;
                }

                var reader = new Reader(data, 4);
                int language = reader.ReadInt();
                int tableCount = reader.ReadInt();
                if (tableCount < 0 || tableCount > MaxTables)
                {
                    error = "表数量异常: " + tableCount;
                    return false;
                }

                var tables = new Dictionary<string, Dictionary<int, string>>(tableCount, StringComparer.Ordinal);
                for (int i = 0; i < tableCount; i++)
                {
                    string name = reader.ReadString(MaxNameBytes);
                    int entryCount = reader.ReadInt();
                    if (entryCount < 0 || entryCount > MaxEntriesPerTable)
                    {
                        error = "表 " + name + " 条目数异常: " + entryCount;
                        return false;
                    }

                    var rows = new Dictionary<int, string>(entryCount);
                    for (int j = 0; j < entryCount; j++)
                    {
                        int id = reader.ReadInt();
                        rows[id] = reader.ReadString(MaxTextBytes);
                    }
                    tables[name] = rows;
                }

                pack = new LanguagePack
                {
                    Language = (LanguageType)language,
                    Tables = tables
                };
                return true;
            }
            catch (Exception e)
            {
                error = "解析语言数据包失败: " + e.Message;
                return false;
            }
        }

        private sealed class Reader
        {
            private readonly byte[] _data;
            private int _offset;

            internal Reader(byte[] data, int offset)
            {
                _data = data;
                _offset = offset;
            }

            internal int ReadInt()
            {
                if (_offset + 4 > _data.Length) throw new InvalidOperationException("数据包被截断");
                int value = _data[_offset]
                            | (_data[_offset + 1] << 8)
                            | (_data[_offset + 2] << 16)
                            | (_data[_offset + 3] << 24);
                _offset += 4;
                return value;
            }

            internal string ReadString(int maxBytes)
            {
                int length = ReadInt();
                if (length < 0 || length > maxBytes) throw new InvalidOperationException("字符串长度异常: " + length);
                if (_offset + length > _data.Length) throw new InvalidOperationException("数据包被截断");
                string value = Encoding.UTF8.GetString(_data, _offset, length);
                _offset += length;
                return value;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace LanguageUnlockMod.Tests
{
    public class LanguagePackTests
    {
        private static byte[] Build(int language, params (string Table, int Id, string Text)[] rows)
        {
            var tables = new Dictionary<string, Dictionary<int, string>>();
            foreach (var row in rows)
            {
                if (!tables.TryGetValue(row.Table, out var entries))
                {
                    entries = new Dictionary<int, string>();
                    tables[row.Table] = entries;
                }
                entries[row.Id] = row.Text;
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(new[] { (byte)'A', (byte)'P', (byte)'L', (byte)'1' });
            writer.Write(language);
            writer.Write(tables.Count);
            foreach (var table in tables)
            {
                WriteString(writer, table.Key);
                writer.Write(table.Value.Count);
                foreach (var entry in table.Value)
                {
                    writer.Write(entry.Key);
                    WriteString(writer, entry.Value);
                }
            }
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        [Fact]
        public void TryParse_ReadsEveryTableAndEntry()
        {
            byte[] bytes = Build(10,
                ("STRCard", 101, "Strike"),
                ("STRCard", 102, "Defend"),
                ("STRSkill", 7, "Boom"));

            Assert.True(LanguagePack.TryParse(bytes, out LanguagePack pack, out string error), error);
            Assert.Equal(LanguageType.English, pack.Language);
            Assert.Equal(2, pack.Tables.Count);
            Assert.Equal("Strike", pack.Tables["STRCard"][101]);
            Assert.Equal("Defend", pack.Tables["STRCard"][102]);
            Assert.Equal("Boom", pack.Tables["STRSkill"][7]);
        }

        [Fact]
        public void TryParse_RejectsForeignMagic()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("NOPE\0\0\0\0\0\0\0\0");
            Assert.False(LanguagePack.TryParse(bytes, out _, out string error));
            Assert.Contains("APL1", error);
        }

        [Fact]
        public void TryParse_RejectsTruncatedPayload()
        {
            byte[] bytes = Build(10, ("STRCard", 1, "Strike"));
            byte[] truncated = new byte[bytes.Length - 3];
            Array.Copy(bytes, truncated, truncated.Length);

            Assert.False(LanguagePack.TryParse(truncated, out _, out string error));
            Assert.Contains("失败", error);
        }

        [Fact]
        public void TryParse_RejectsAbsurdTableCount()
        {
            byte[] bytes = Build(10, ("STRCard", 1, "Strike"));
            bytes[8] = 0xFF;
            bytes[9] = 0xFF;
            bytes[10] = 0xFF;
            bytes[11] = 0x7F;

            Assert.False(LanguagePack.TryParse(bytes, out _, out string error));
            Assert.Contains("表数量异常", error);
        }
    }
}

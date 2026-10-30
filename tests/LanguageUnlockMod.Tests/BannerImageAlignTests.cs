using Google.Protobuf.Collections;
using Xunit;

namespace LanguageUnlockMod.Tests
{
    public class BannerImageAlignTests
    {
        private static RepeatedField<string> Images(params string[] values)
        {
            var field = new RepeatedField<string>();
            foreach (string value in values) field.Add(value);
            return field;
        }

        [Fact]
        public void Align_PointsForeignSlotsBackToSimplified()
        {
            var images = Images("UT_Banner_1_CN", "UT_Banner_1_EN", "UT_Banner_1_JP", "UT_Banner_1_TC");

            Assert.Equal(3, BannerImageAlign.Align(images));

            Assert.Equal("UT_Banner_1_CN", images[0]);
            Assert.Equal("UT_Banner_1_CN", images[1]);
            Assert.Equal("UT_Banner_1_CN", images[2]);
            Assert.Equal("UT_Banner_1_CN", images[3]);
        }

        [Fact]
        public void Align_IsIdempotent()
        {
            var images = Images("cn", "en", "jp", "tc");

            Assert.Equal(3, BannerImageAlign.Align(images));
            Assert.Equal(0, BannerImageAlign.Align(images));
        }

        [Fact]
        public void Align_IgnoresShortOrEmptyLists()
        {
            Assert.Equal(0, BannerImageAlign.Align(null));
            Assert.Equal(0, BannerImageAlign.Align(Images()));
            Assert.Equal(0, BannerImageAlign.Align(Images("cn", "en")));
            Assert.Equal(0, BannerImageAlign.Align(Images("", "en", "jp", "tc")));
        }

        [Fact]
        public void Align_KeepsSlotsThatAlreadyMatch()
        {
            var images = Images("cn", "cn", "jp", "tc");
            Assert.Equal(2, BannerImageAlign.Align(images));
        }
    }
}

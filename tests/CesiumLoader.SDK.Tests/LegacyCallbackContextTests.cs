using System;
using System.Reflection;
using CesiumLoader.SDK.Mods;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class LegacyCallbackContextTests
    {
        private static void Callback() { }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RegistrationUsesCallbackAssemblyEvenWhenInvokedThroughReflection(bool hasInit)
        {
            var method = typeof(ModBase).GetMethod("RegisterLegacyCallbacks", BindingFlags.Static | BindingFlags.NonPublic);
            Action callback = Callback;
            var context = (ModContext)method.Invoke(null, new object[] { hasInit ? callback : null, hasInit ? null : callback });
            Assert.NotNull(context);
            Assert.Same(typeof(LegacyCallbackContextTests).Assembly, context.Assembly);
            Assert.Same(context, ModRegistry.ForAssembly(typeof(LegacyCallbackContextTests).Assembly));
            Assert.NotEqual(typeof(ModBase).Assembly, context.Assembly);
        }
    }
}

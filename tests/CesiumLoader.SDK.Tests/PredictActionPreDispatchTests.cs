using System;
using CesiumLoader.SDK.Gameplay;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class PredictActionPreDispatchTests
    {
        [Theory]
        [InlineData(1, false, true, true)]
        [InlineData(0, false, true, false)]
        [InlineData(-1, false, true, false)]
        [InlineData(1, true, true, false)]
        [InlineData(1, false, false, false)]
        public void OnlyLocalBroadcastActionsWithSnAreEligible(
            long sn, bool notBroadcast, bool local, bool expected)
        {
            Assert.Equal(expected, PredictActionPreDispatch.IsEligible(sn, notBroadcast, local));
        }

        [Fact]
        public void NoHandlerNeverClaimsAnAction()
        {
            var action = new PredictActionRequest(8000, 42, 100, new byte[] { 7 });
            Assert.False(PredictActionPreDispatch.DispatchHandlers(null, action));
            Assert.False(PredictActionPreDispatch.DispatchHandlers(null, null));
        }

        [Fact]
        public void FirstSuccessfulHandlerStopsFurtherHandlers()
        {
            int count = 0;
            Func<PredictActionRequest, bool> handler = _ => { count++; return false; };
            handler += _ => { count++; return true; };
            handler += _ => { count++; return true; };
            var action = new PredictActionRequest(8000, 42, 100, new byte[] { 7 });
            Assert.True(PredictActionPreDispatch.DispatchHandlers(handler, action));
            Assert.Equal(2, count);
        }

        [Fact]
        public void AllHandlersReturningFalseNeverClaimAnAction()
        {
            int count = 0;
            Func<PredictActionRequest, bool> handler = _ => { count++; return false; };
            handler += _ => { count++; return false; };
            var action = new PredictActionRequest(8000, 42, 100, new byte[] { 7 });
            Assert.False(PredictActionPreDispatch.DispatchHandlers(handler, action));
            Assert.Equal(2, count);
        }

        [Fact]
        public void ThrowingHandlerDoesNotBlockLaterHandlers()
        {
            int after = 0;
            Func<PredictActionRequest, bool> handler = _ => throw new InvalidOperationException("boom");
            handler += _ => { after++; return true; };
            var action = new PredictActionRequest(8000, 42, 100, new byte[] { 7 });
            Assert.True(PredictActionPreDispatch.DispatchHandlers(handler, action));
            Assert.Equal(1, after);
        }

        [Fact]
        public void PayloadIsNotSharedOrMutableAcrossReads()
        {
            var original = new byte[] { 1, 2, 3 };
            var action = new PredictActionRequest(8000, 42, 100, original);
            original[0] = 9;
            byte[] a = action.Data;
            a[1] = 9;
            Assert.Equal(new byte[] { 1, 2, 3 }, action.Data);
        }

        [Fact]
        public void NullPayloadBecomesEmptyArray()
        {
            var action = new PredictActionRequest(8000, 42, 100, null);
            Assert.Empty(action.Data);
        }

        [Fact]
        public void SeparateRequestsDoNotSharePayload()
        {
            var payload = new byte[] { 5 };
            var a = new PredictActionRequest(1, 2, 3, payload);
            var b = new PredictActionRequest(1, 2, 4, payload);
            a.Data[0] = 8;
            Assert.Equal(5, b.Data[0]);
        }
    }
}

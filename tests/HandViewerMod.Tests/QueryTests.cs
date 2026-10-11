using System;
using System.Collections.Generic;
using System.Reflection;
using HandViewerMod;
using CesiumLoader.SDK.Gameplay;
using party.model;
using party.protocol;
using Xunit;

namespace HandViewerMod.Tests
{
    public class QueryTests
    {
        const string Room = "9223372036854775806";
        const string Valid = "{\"officialSpectating\":true,\"roomId\":\"9223372036854775806\",\"sampledUtc\":\"2026-01-01T00:00:00Z\",\"players\":[{\"playerId\":\"9223372036854775805\",\"handKnown\":true,\"handCount\":1,\"cards\":[{\"cardUid\":12,\"cardId\":20008,\"purifyNum\":2,\"isTemp\":true,\"battleCost\":3}]}]}";
        sealed class Request : IHandRequest
        {
            public bool Done { get; set; }
            public long Code { get; set; } = 200;
            public string Text { get; set; } = Valid;
            public string RetryAfter { get; set; }
            public bool Disposed;
            public void Dispose() { Disposed = true; }
        }
        [Fact] public void PreservesLongIdsAndCardProperties()
        {
            var snapshot = HandSnapshot.Parse(Valid, Room);
            Assert.Equal(Room, snapshot.roomId);
            Assert.Equal("9223372036854775805", snapshot.players[0].playerId);
            Assert.Equal(2, snapshot.players[0].cards[0].purifyNum);
            Assert.True(snapshot.players[0].cards[0].isTemp);
        }
        [Fact] public void EmptyKnownHandIsNotUnknown()
        {
            var json = Valid.Replace("\"handCount\":1", "\"handCount\":0");
            int start = json.IndexOf("\"cards\":[");
            json = json.Substring(0, start) + "\"cards\":[]}]}";
            var snapshot = HandSnapshot.Parse(json, Room);
            Assert.True(snapshot.players[0].handKnown); Assert.Empty(snapshot.players[0].cards);
        }
        [Fact] public void UnknownHandNeverDisplaysCards()
        {
            var snapshot = HandSnapshot.Parse(Valid.Replace("\"handKnown\":true", "\"handKnown\":false"), Room);
            Assert.False(snapshot.players[0].handKnown); Assert.Empty(snapshot.players[0].cards);
        }
        [Theory]
        [InlineData("\"officialSpectating\":true", "\"officialSpectating\":false")]
        [InlineData("\"handCount\":1", "\"handCount\":2")]
        [InlineData("\"cardUid\":12", "\"cardUid\":0")]
        [InlineData("\"cardId\":20008", "\"cardId\":-1")]
        [InlineData("\"purifyNum\":2", "\"purifyNum\":-1")]
        [InlineData("\"cardId\":20008", "\"cardId\":20008.5")]
        [InlineData("\"cardId\":20008", "\"cardId\":\"20008\"")]
        [InlineData("\"handKnown\":true", "\"handKnown\":\"true\"")]
        [InlineData("\"isTemp\":true", "\"isTemp\":1")]
        [InlineData("\"playerId\":\"9223372036854775805\"", "\"playerId\":\"0\"")]
        public void RejectsInvalidFields(string old, string replacement) => Assert.ThrowsAny<Exception>(() => HandSnapshot.Parse(Valid.Replace(old, replacement), Room));
        [Fact] public void RejectsWrongRoom() => Assert.ThrowsAny<Exception>(() => HandSnapshot.Parse(Valid, "1"));
        [Theory] [InlineData("")] [InlineData("not json")] [InlineData("[]")] [InlineData("{}")]
        public void RejectsBadResponse(string json) => Assert.ThrowsAny<Exception>(() => HandSnapshot.Parse(json, Room));
        [Fact] public void RejectsOversizedResponse() => Assert.ThrowsAny<Exception>(() => HandSnapshot.Parse(new string(' ', 1024 * 1024 + 1), Room));
        [Fact] public void VisibleHandStartsQueryAndSharesSnapshot()
        {
            int calls = 0; var request = new Request();
            using (var controller = new HandQueryController(code => { Assert.Equal("synthetic-code", code); calls++; return request; }))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, "synthetic-code", 0); controller.Tick(0);
                for (int i = 1; i < 10; i++) controller.Tick(i);
                Assert.Equal(1, calls); Assert.True(controller.Running);
                request.Done = true; controller.Tick(10);
                Assert.NotNull(controller.Snapshot); Assert.True(request.Disposed); Assert.False(controller.Running);
            }
        }
        [Fact] public void CodeArrivingLaterStartsQuery()
        {
            int calls = 0;
            using (var controller = new HandQueryController(_ => { calls++; return new Request(); }))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, null, 0); controller.Tick(0); Assert.Equal(0, calls);
                controller.SetViewing(true, 0); controller.SetRoom(Room, "synthetic", 10); controller.Tick(10); Assert.Equal(1, calls);
            }
        }
        [Fact] public void DirtyDuringRequestCausesOnlyOneFollowup()
        {
            int calls = 0; var first = new Request();
            using (var controller = new HandQueryController(_ => { calls++; return calls == 1 ? first : new Request(); }))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0); controller.Tick(0);
                controller.MarkDirty(100); controller.MarkDirty(200); first.Done = true; controller.Tick(300);
                controller.Tick(1000); Assert.Equal(1, calls);
                controller.Tick(1800); Assert.Equal(2, calls);
            }
        }
        [Theory] [InlineData(429)] [InlineData(503)]
        public void RespectsRetryAfter(int status)
        {
            int calls = 0;
            using (var controller = new HandQueryController(_ => { calls++; return new Request { Done = true, Code = status, RetryAfter = "10" }; }))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0); controller.Tick(0); controller.Tick(1);
                controller.Tick(10000); Assert.Equal(1, calls);
                controller.Tick(10001); Assert.Equal(2, calls);
            }
        }
        [Theory]
        [InlineData("你没有权限获得卡牌信息", "你没有权限获得卡牌信息")]
        [InlineData("{\"message\":\"你没有权限获得卡牌信息\",\"error\":\"forbidden\"}", "你没有权限获得卡牌信息")]
        [InlineData("{\"detail\":\"权限不足\"}", "权限不足")]
        [InlineData("{\"error\":\"权限不足\"}", "权限不足")]
        [InlineData("{\"error\":{\"message\":\"权限不足\"}}", "权限不足")]
        public void DisplaysServerErrorMessage(string body, string expected)
        {
            var request = new Request { Done = true, Code = 403, Text = body };
            using var controller = new HandQueryController(_ => request);
            controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0);
            controller.Tick(0); controller.Tick(1);
            Assert.Equal("查询失败 HTTP 403：" + expected + "（稍后重试）", controller.Status);
            Assert.True(request.Disposed); Assert.False(controller.Running);
        }
        [Theory]
        [InlineData("")]
        [InlineData("{\"code\":403}")]
        [InlineData("{broken")]
        [InlineData("<html>Forbidden</html>")]
        public void MissingServerMessageKeepsHttpFallback(string body)
        {
            using var controller = new HandQueryController(_ => new Request { Done = true, Code = 403, Text = body });
            controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0);
            controller.Tick(0); controller.Tick(1);
            Assert.Equal("查询失败 HTTP 403（稍后重试）", controller.Status);
        }
        [Fact] public void BusinessErrorWithHttp200DisplaysServerMessage()
        {
            using var controller = new HandQueryController(_ => new Request { Done = true, Text = "{\"message\":\"权限不足\",\"code\":403}" });
            controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0);
            controller.Tick(0); controller.Tick(1);
            Assert.Equal("查询失败：权限不足（稍后重试）", controller.Status);
            Assert.Null(controller.Snapshot);
        }
        [Fact] public void ServerErrorKeepsOldSnapshotAndRecoversOnRetry()
        {
            int calls = 0;
            using var controller = new HandQueryController(_ => new Request { Done = true,
                Code = ++calls == 2 ? 403 : 200, Text = calls == 2 ? "权限不足" : Valid });
            controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0);
            controller.Tick(0); controller.Tick(1);
            var snapshot = controller.Snapshot;
            controller.Refresh(1501); controller.Tick(1501); controller.Tick(1502);
            Assert.Same(snapshot, controller.Snapshot); Assert.True(controller.IsStale);
            Assert.Equal("查询失败 HTTP 403：权限不足（显示旧快照，稍后重试）", controller.Status);
            controller.Tick(3501); Assert.Equal(2, calls);
            controller.Tick(3502); controller.Tick(3503);
            Assert.Equal(3, calls); Assert.Equal("已采样", controller.Status); Assert.False(controller.IsStale);
        }
        [Fact] public void ServerMessageIsBoundedAndControlCharactersAreRemoved()
        {
            using var controller = new HandQueryController(_ => new Request { Done = true, Code = 403, Text = "拒绝\n" + new string('长', 600) });
            controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0);
            controller.Tick(0); controller.Tick(1);
            Assert.Contains("拒绝 长", controller.Status); Assert.Contains("…", controller.Status);
            Assert.DoesNotContain("\n", controller.Status); Assert.True(controller.Status.Length < 600);
        }
        [Fact] public void RoomChangeDisposesOldRequestAndRejectsLateResult()
        {
            var request = new Request(); int calls = 0;
            using (var controller = new HandQueryController(_ => { calls++; return request; }))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0); controller.Tick(0);
                controller.SetRoom(null, null, 1); request.Done = true; controller.Tick(2);
                Assert.True(request.Disposed); Assert.Null(controller.Snapshot); Assert.Equal(1, calls);
            }
        }
        [Fact] public void NetworkFailureDoesNotSpinEachFrame()
        {
            int calls = 0;
            using (var controller = new HandQueryController(_ => { calls++; throw new Exception(); }))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0);
                for (int i = 0; i < 1000; i++) controller.Tick(i);
                Assert.Equal(1, calls);
            }
        }
        [Fact] public void RejectsTrailingJson() => Assert.ThrowsAny<Exception>(() => HandSnapshot.Parse(Valid + "{}", Room));
        [Fact] public void RejectsDeepNesting() => Assert.ThrowsAny<Exception>(() => HandSnapshot.Parse(new string('[', 100) + new string(']', 100), Room));
        [Fact] public void NegativeOneCostMeansUseConfiguredBaseCost()
        {
            var snapshot = HandSnapshot.Parse(Valid.Replace("\"battleCost\":3", "\"battleCost\":-1"), Room);
            Assert.Equal(-1, snapshot.players[0].cards[0].battleCost);
        }
        [Fact] public void DirtySnapshotIsExplicitlyStale()
        {
            var request = new Request { Done = true };
            using (var controller = new HandQueryController(_ => request))
            {
                controller.SetViewing(true, 0); controller.SetRoom(Room, "test", 0); controller.Tick(0); controller.Tick(1);
                Assert.False(controller.IsStale);
                controller.MarkDirty(2); Assert.True(controller.IsStale);
            }
        }
        [Fact] public void CollapsedHandsNeverQueryEvenOnEventsOrManualRefresh()
        {
            int calls = 0;
            using (var controller = new HandQueryController(_ => { calls++; return new Request(); }))
            {
                controller.SetRoom(Room, "test", 0);
                controller.MarkDirty(1); controller.Refresh(2); controller.Tick(10000);
                Assert.Equal(0, calls);
                controller.SetViewing(true, 10001); controller.Tick(10001);
                Assert.Equal(1, calls);
            }
        }
        [Fact] public void ClosingCancelsRequestAndReopeningQueriesFreshData()
        {
            int calls = 0; var first = new Request();
            using (var controller = new HandQueryController(_ => { calls++; return calls == 1 ? first : new Request(); }))
            {
                controller.SetRoom(Room, "test", 0); controller.SetViewing(true, 0); controller.Tick(0);
                controller.MarkDirty(1); controller.SetViewing(false, 2);
                Assert.True(first.Disposed); Assert.False(controller.Running);
                first.Done = true; controller.MarkDirty(3); controller.Tick(10000);
                Assert.Equal(1, calls); Assert.Null(controller.Snapshot);
                controller.SetViewing(true, 10001); controller.Tick(10001);
                Assert.Equal(2, calls);
            }
        }
        [Fact] public void ClosingPausesFailureRetriesAndKeepsCachedSnapshotStale()
        {
            int calls = 0;
            using (var controller = new HandQueryController(_ => {
                calls++; return new Request { Done = true, Code = calls == 1 ? 200 : 503, RetryAfter = "10" };
            }))
            {
                controller.SetRoom(Room, "test", 0); controller.SetViewing(true, 0);
                controller.Tick(0); controller.Tick(1);
                controller.Refresh(1501); controller.Tick(1501); controller.Tick(1502);
                controller.SetViewing(false, 1503); controller.Tick(30000);
                Assert.Equal(2, calls); Assert.NotNull(controller.Snapshot); Assert.True(controller.IsStale);
                controller.SetViewing(true, 30001); controller.Tick(30001);
                Assert.Equal(3, calls);
            }
        }
        [Fact] public void RemainingVisibleHandDoesNotCancelSharedRequest()
        {
            var request = new Request(); int calls = 0;
            using (var controller = new HandQueryController(_ => { calls++; return request; }))
            {
                controller.SetRoom(Room, "test", 0); controller.SetViewing(true, 0); controller.Tick(0);
                controller.SetViewing(true, 1); controller.Tick(1);
                Assert.False(request.Disposed); Assert.Equal(1, calls);
                request.Done = true; controller.Tick(2); controller.Tick(5000);
                Assert.NotNull(controller.Snapshot); Assert.Equal(1, calls);
            }
        }
        [Fact] public void EmptyCardChangeStillNotifiesSdkSubscribers()
        {
            int calls = 0;
            Action<long, IReadOnlyList<CardInfo>> handler = (id, cards) => { calls++; Assert.Empty(cards); };
            GameEvents.HandChanged += handler;
            try
            {
                var method = typeof(GameEvents).GetMethod("OnHeroCardChange", BindingFlags.Static | BindingFlags.NonPublic);
                method.Invoke(null, new object[] { new RoomHeroCardChangeS2C(), 0, true });
                method.Invoke(null, new object[] { new RoomHeroCardChangeS2C(), 1, true });
                Assert.Equal(1, calls);
            }
            finally { GameEvents.HandChanged -= handler; }
        }
    }
}

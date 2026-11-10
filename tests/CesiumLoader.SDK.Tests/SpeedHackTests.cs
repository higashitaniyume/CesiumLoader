using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CesiumLoader.SDK.Speed;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class SpeedHackTests : IDisposable
    {
        private readonly Socket _peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private EndPoint _client = new IPEndPoint(IPAddress.Loopback, 0);
        private readonly SpeedUdpChannel _channel = new SpeedUdpChannel();
        private double _now = 100;
        private int Port => ((IPEndPoint)_peer.LocalEndPoint).Port;
        public SpeedHackTests()
        {
            _peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _peer.ReceiveTimeout = 1000;
            _channel.Clock = () => _now;
            SpeedHack.PortOverride = Port; SpeedHack.ResetCache();
        }
        public void Dispose()
        {
            _channel.Reset(); SpeedHack.PortOverride = 0; SpeedHack.ResetCache(); _peer.Close();
        }
        private string Receive()
        {
            byte[] bytes = new byte[1024];
            int n = _peer.ReceiveFrom(bytes, ref _client);
            return Encoding.ASCII.GetString(bytes, 0, n);
        }
        private static long Id(string request) => long.Parse(request.Split(' ')[request.StartsWith("set ") ? 3 : 1], CultureInfo.InvariantCulture);
        private static string Reply(long id, string speed = "2.000", long revision = 1, string extra = "") =>
            "protocol=1\nid=" + id + "\nrevision=" + revision + "\nok=1\nversion=3.5.2\nspeed=" + speed + "\nbase=2.000\nactive=1\nhooks=4\n" + extra;
        private void Send(string reply) => _peer.SendTo(Encoding.ASCII.GetBytes(reply), _client);
        private SpeedUdpChannel.Snapshot Pump()
        {
            SpeedUdpChannel.Snapshot result = null;
            Assert.True(SpinWait.SpinUntil(() => (result = _channel.Read(Port)) != null, 1000));
            return result;
        }
        private void Ready(string speed = "2.000", long revision = 1)
        {
            Assert.Null(_channel.Read(Port)); Send(Reply(Id(Receive()), speed, revision)); Pump();
        }

        [Fact]
        public void UdpQueryAndSdkFacadeReadActualState()
        {
            Assert.False(SpeedHack.IsAvailable);
            Send(Reply(Id(Receive()), "2.500"));
            Assert.True(SpinWait.SpinUntil(() => SpeedHack.IsAvailable, 1000));
            Assert.True(SpeedHack.TryGetSpeed(out double speed)); Assert.Equal(2.5, speed);
            Assert.Equal(2, SpeedHack.BaseSpeed); Assert.Null(SpeedHack.UnavailableReason);
            Assert.Contains("UDP 127.0.0.1:", SpeedHack.Describe());
        }
        [Fact]
        public void SetInvalidatesOldAcknowledgementAndOnlyItsOwnReplyCanConfirm()
        {
            Ready("1.000");
            _now += .06; _channel.Read(Port); long oldQuery = Id(Receive());
            Assert.True(_channel.Set(Port, 4)); long mutation = Id(Receive());
            Send(Reply(oldQuery, "1.000"));
            Assert.Null(_channel.Read(Port));
            Send(Reply(mutation, "4.000", 2));
            Assert.Equal(4, Pump().Speed);
        }
        [Fact]
        public void LostMutationRetriesSameIdBeforeSendingAnyQuery()
        {
            Ready(); Assert.True(_channel.Set(Port, 1)); string request = Receive();
            Assert.StartsWith("set ", request);
            _now += .06; Assert.Null(_channel.Read(Port)); string retry = Receive();
            Assert.Equal(request.Substring(0, request.LastIndexOf(' ')), retry.Substring(0, retry.LastIndexOf(' ')));
            Assert.NotEqual(Id(request), Id(retry));
            Send(Reply(Id(retry), "1.000", 2)); Assert.Equal(1, Pump().Speed);
        }
        [Fact]
        public void OldMutationReplyCannotBorrowFreshnessFromARetry()
        {
            Ready(); _channel.Set(Port, 1); long old = Id(Receive());
            _now += .6; Assert.Null(_channel.Read(Port)); long retry = Id(Receive());
            Send(Reply(old, "1.000", 2)); Assert.Null(_channel.Read(Port));
            Send(Reply(retry, "1.000", 2)); Assert.Equal(1, Pump().Speed);
        }
        [Fact]
        public void SparseStartupConsumerStillGetsFreshBackgroundReplies()
        {
            var channel = new SpeedUdpChannel(background: true);
            try
            {
                Assert.Null(channel.Read(Port));
                // Service the loader side without calling Read on the consumer.
                var until = DateTime.UtcNow.AddMilliseconds(800);
                while (DateTime.UtcNow < until) Send(Reply(Id(Receive()), "3.000"));
                Thread.Sleep(100);
                Assert.Equal(3, channel.Read(Port)?.Speed);
            }
            finally { channel.Reset(); }
        }
        [Fact]
        public void UnacknowledgedMutationTimesOutAndRecoversByQueryingActualState()
        {
            Ready(); _channel.Set(Port, 1); Receive();
            _now += 2.01; Assert.Null(_channel.Read(Port));
            string query = Receive(); Assert.StartsWith("get ", query);
            Send(Reply(Id(query), "4.000", 2)); Assert.Equal(4, Pump().Speed);
        }
        [Fact]
        public void NewMutationSupersedesOldReply()
        {
            Ready(); _channel.Set(Port, 1); long old = Id(Receive());
            _channel.Set(Port, 5); long current = Id(Receive());
            Send(Reply(old, "1.000", 2)); Assert.Null(_channel.Read(Port));
            Send(Reply(current, "5.000", 3)); Assert.Equal(5, Pump().Speed);
        }
        [Fact]
        public void MissingPeerExpiresCachedSpeedInsteadOfAcknowledgingOne()
        {
            Ready("1.000"); _now += .51;
            Assert.Null(_channel.Read(Port));
            Assert.Contains("过期", _channel.Error);
            long current = Id(Receive()); Send(Reply(current, "4.000", 2)); Assert.Equal(4, Pump().Speed);
        }
        [Fact]
        public void DelayedReplyCannotRenewItsOwnFreshness()
        {
            Assert.Null(_channel.Read(Port)); long old = Id(Receive());
            _now += .6; Send(Reply(old, "1.000"));
            Assert.Null(_channel.Read(Port));
            Send(Reply(Id(Receive()), "3.000", 2)); Assert.Equal(3, Pump().Speed);
        }
        [Fact]
        public void LowerRevisionAndUnsolicitedIdsAreIgnored()
        {
            Ready("4.000", 5); _now += .06; _channel.Read(Port); long id = Id(Receive());
            Send(Reply(id + 10, "1.000", 6)); Send(Reply(id, "1.000", 4));
            Assert.Equal(4, _channel.Read(Port).Speed);
            Send(Reply(id, "2.000", 6));
            Assert.True(SpinWait.SpinUntil(() => _channel.Read(Port)?.Speed == 2, 1000));
        }
        [Fact]
        public void EndpointChangeAndClockRollbackInvalidateState()
        {
            Ready(); Assert.Null(_channel.Read(0));
            Ready(); _now -= 1; Assert.Null(_channel.Read(Port));
            Send(Reply(Id(Receive()), "3.000")); Assert.Equal(3, Pump().Speed);
        }
        [Fact]
        public void LoopbackClientsAreIsolatedEvenWithSameLoaderEndpoint()
        {
            Ready("2.000");
            using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            // A datagram from another local program must not masquerade as an ack.
            other.SendTo(Encoding.ASCII.GetBytes(Reply(long.MaxValue, "1.000")), _client);
            Assert.Equal(2, _channel.Read(Port).Speed);
        }
        [Theory]
        [InlineData("0.500")][InlineData("NaN")][InlineData("Infinity")][InlineData("1e1")]
        [InlineData("0x2")][InlineData("2,5")][InlineData("101")][InlineData("1.")]
        public void InvalidStateNeverBecomesAnAcknowledgement(string speed)
        {
            Assert.Null(SpeedUdpChannel.Parse(Reply(1, speed)));
        }
        [Theory]
        [InlineData("speed=1\n")][InlineData("hooks=4\n")][InlineData("id=1\n")]
        [InlineData("garbage")][InlineData("protocol=2\n")]
        public void DuplicatedFieldsAndMalformedRepliesAreRejected(string extra)
        {
            Assert.Null(SpeedUdpChannel.Parse(Reply(1, extra: extra)));
        }
        [Theory]
        [InlineData("hooks=0", "hooks=4")][InlineData("hooks=1.5", "hooks=4")]
        [InlineData("active=0", "active=1")][InlineData("id=0", "id=1")]
        [InlineData("id=9223372036854775808", "id=1")]
        public void InvalidMetadataIsRejected(string replacement, string original)
        {
            Assert.Null(SpeedUdpChannel.Parse(Reply(1).Replace(original, replacement)));
        }
        [Theory]
        [InlineData(.5)][InlineData(.999)][InlineData(0)][InlineData(-1)]
        [InlineData(101)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
        public void SdkRejectsInvalidSpeedsWithoutSending(double speed)
        {
            Assert.False(SpeedHack.SetSpeed(speed)); Assert.Equal(0, _peer.Available);
        }
        [Fact]
        public void SdkSerializesInvariantDecimalsAndCanSendBeforeFirstReply()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(SpeedHack.SetSpeed(2.5)); Assert.Equal("2.500", Receive().Split(' ')[2]);
                Assert.False(SpeedHack.TryGetSpeed(out _));
                Assert.True(SpeedHack.Reset()); Assert.Equal("1.000", Receive().Split(' ')[2]);
            }
            finally { CultureInfo.CurrentCulture = old; }
        }
        [Fact]
        public void MissingPortHasSafeDefaultsAndUsefulDiagnostics()
        {
            SpeedHack.PortOverride = -1;
            Assert.False(SpeedHack.IsAvailable); Assert.False(SpeedHack.TryGetSpeed(out _));
            Assert.Equal(1, SpeedHack.Speed); Assert.Equal(1, SpeedHack.BaseSpeed);
            Assert.False(SpeedHack.SetSpeed(2)); Assert.Contains("CESIUM_SPEED_PORT", SpeedHack.Diagnostics());
        }

        [Fact]
        public void ProcessEnvironmentDiscoversPortWithoutDirectoryFallback()
        {
            string saved = Environment.GetEnvironmentVariable("CESIUM_SPEED_PORT");
            try
            {
                SpeedHack.PortOverride = 0;
                Environment.SetEnvironmentVariable("CESIUM_SPEED_PORT", Port.ToString(CultureInfo.InvariantCulture));
                SpeedHack.Refresh(); Assert.False(SpeedHack.IsAvailable);
                Send(Reply(Id(Receive()), "2.500"));
                Assert.True(SpinWait.SpinUntil(() => SpeedHack.TryGetSpeed(out double speed) && speed == 2.5, 1000));
                Environment.SetEnvironmentVariable("CESIUM_SPEED_PORT", "70000");
                Assert.False(SpeedHack.IsAvailable);
                Environment.SetEnvironmentVariable("CESIUM_SPEED_PORT", Port.ToString(CultureInfo.InvariantCulture));
                Assert.False(SpeedHack.IsAvailable);
                Send(Reply(Id(Receive()), "3.000"));
                Assert.True(SpinWait.SpinUntil(() => SpeedHack.TryGetSpeed(out double speed) && speed == 3, 1000));
            }
            finally { Environment.SetEnvironmentVariable("CESIUM_SPEED_PORT", saved); }
        }

        [Fact]
        public void HotUpdateTransportContainsNoFileOrPInvokeFallback()
        {
            var assembly = typeof(SpeedHackTests).Assembly;
            int checkedSources = 0;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith("SpeedHack.cs") && !name.EndsWith("SpeedUdpChannel.cs")) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                string code = reader.ReadToEnd();
                foreach (string forbidden in new[] { "File.", "FileStream", "FileInfo", "DirectoryInfo", "DllImport", "ReadAllText", "WriteAllText", "request.txt", "state.txt", "ReceiveTimeout", "Stopwatch" })
                    Assert.DoesNotContain(forbidden, code);
                checkedSources++;
            }
            Assert.Equal(2, checkedSources);
        }

        [Theory]
        [InlineData(2, 1, .5, 1, 3, 2.5)][InlineData(2, -1, .5, 1, 3, 1.5)]
        [InlineData(2, 1, .25, 1, 3, 2.25)][InlineData(3.8, 1, .5, .5, 4, 4)]
        [InlineData(.6, -1, .5, .5, 4, 1)][InlineData(2.5, 1, .5, 4, .5, 3)]
        [InlineData(1.5, -1, .5, 4, .5, 1)][InlineData(2, 0, .5, 1, 3, 2)]
        [InlineData(double.NaN, 1, .5, 1, 3, 1.5)][InlineData(2, double.NaN, .5, 1, 3, 2)]
        [InlineData(double.PositiveInfinity, 1, .5, 1, 3, 1.5)]
        [InlineData(2, double.PositiveInfinity, .5, 1, 3, 2)]
        public void StepUsesBoundsAndSafeDefaults(double current, double delta, double step, double min, double max, double expected)
        { Assert.Equal(expected, SpeedHack.StepSpeed(current, delta, step, min, max), 8); }
        [Theory]
        [InlineData(0)][InlineData(-1)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
        public void BadStepAndBoundsFallBack(double value)
        {
            Assert.Equal(2.5, SpeedHack.StepSpeed(2, 1, value));
            Assert.Equal(1, SpeedHack.StepSpeed(1, -1, 100, value, 8));
            Assert.Equal(SpeedHack.MaxSpeed, SpeedHack.StepSpeed(1, 1, 100, .5, value));
        }
        [Fact]
        public void ClampPreservesHardFloorAndDecimalRounding()
        {
            Assert.Equal(1, SpeedHack.ClampSpeed(.5, .1, 10));
            Assert.Equal(1, SpeedHack.ClampSpeed(.5, 5, .1));
            Assert.Equal(1, SpeedHack.ClampSpeed(double.NaN));
            Assert.Equal(1, SpeedHack.ClampSpeed(double.PositiveInfinity));
            Assert.Equal(3, SpeedHack.ClampSpeed(999));
            double speed = 2;
            for (int i = 0; i < 10; i++) speed = SpeedHack.StepSpeed(speed, 1, .1, 1, 10);
            Assert.Equal(3, speed); Assert.Equal(2.5, SpeedHack.ClampSpeed(2.5000000000000004));
            Assert.Equal(1, SpeedHack.MinSpeed); Assert.Equal(100, SpeedHack.HardMaxSpeed);
        }
    }
}

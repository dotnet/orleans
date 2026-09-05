using System;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    [TestSuite("BVT")]
    [TestProvider("ZooKeeper")]
    [TestArea("Membership")]
    public sealed class MembershipSerializerSettingsUnitTests
    {
        private static readonly DateTime StartTime =
            new(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

        private static readonly DateTime IAmAliveTime =
            new(2024, 1, 2, 3, 5, 6, 789, DateTimeKind.Utc);

        private static readonly DateTime SuspectTime =
            new(2024, 1, 2, 3, 6, 7, 890, DateTimeKind.Utc);

        [Fact]
        public void MembershipEntry_Serialization_UsesUtf8NewtonsoftFormattingNoneAndStableSchema()
        {
            var payload = Serialize(CreateMembershipEntry());
            var json = Encoding.UTF8.GetString(payload);
            var row = JObject.Parse(json);

            Assert.Equal(
                ["SiloAddress", "HostName", "SiloName", "InstanceName", "Status", "ProxyPort", "StartTime", "SuspectTimes"],
                row.Properties().Select(property => property.Name));
            Assert.Equal("Active", row["Status"]!.Value<string>());
            Assert.Equal(30000, row["ProxyPort"]!.Value<int>());
            Assert.Equal(StartTime, row["StartTime"]!.Value<DateTime>());
            Assert.Null(row.Property("IAmAliveTime"));
            Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
            Assert.True(
                payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes("ø")) >= 0,
                "The payload should contain the UTF-8 byte sequence for ø.");
            Assert.True(
                payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes("東京")) >= 0,
                "The payload should contain the UTF-8 byte sequence for 東京.");
            Assert.Equal("høst-東京", row["HostName"]!.Value<string>());
        }

        [Fact]
        public void MembershipEntry_RoundTrip_PreservesPersistedFields()
        {
            var original = CreateMembershipEntry();

            var roundTrip = Deserialize<MembershipEntry>(Serialize(original));

            Assert.Equal(original.SiloAddress.ToParsableString(), roundTrip.SiloAddress.ToParsableString());
            Assert.Equal(original.HostName, roundTrip.HostName);
            Assert.Equal(original.SiloName, roundTrip.SiloName);
            Assert.Equal(SiloStatus.Active, roundTrip.Status);
            Assert.Equal(30000, roundTrip.ProxyPort);
            Assert.Equal(StartTime, roundTrip.StartTime);
            var suspect = Assert.Single(roundTrip.SuspectTimes!);
            Assert.Equal("127.0.0.2:11112@54321", suspect.Item1.ToParsableString());
            Assert.Equal(SuspectTime, suspect.Item2);
        }

        [Fact]
        public void MembershipEntry_Serialization_WritesSiloNameAndLegacyInstanceName()
        {
            var row = JObject.Parse(Encoding.UTF8.GetString(Serialize(CreateMembershipEntry())));

            Assert.Equal("silo-modern", row["SiloName"]!.Value<string>());
            Assert.Equal("silo-modern", row["InstanceName"]!.Value<string>());
            Assert.NotSame(row.Property("SiloName"), row.Property("InstanceName"));
        }

        [Fact]
        public void MembershipEntry_Deserialization_WithOnlyInstanceName_UsesLegacyValue()
        {
            var row = CreateSerializedRow();
            row.Remove("SiloName");
            row["InstanceName"] = "silo-legacy";

            var entry = Deserialize<MembershipEntry>(Encoding.UTF8.GetBytes(row.ToString(Formatting.None)));

            Assert.Equal("silo-legacy", entry.SiloName);
            Assert.Equal("127.0.0.1:11111@12345", entry.SiloAddress.ToParsableString());
        }

        [Fact]
        public void MembershipEntry_Deserialization_WithBothNames_PrefersSiloName()
        {
            var row = CreateSerializedRow();
            row["SiloName"] = "silo-modern";
            row["InstanceName"] = "silo-legacy-different";

            var entry = Deserialize<MembershipEntry>(Encoding.UTF8.GetBytes(row.ToString(Formatting.None)));

            Assert.Equal("silo-modern", entry.SiloName);
            Assert.Equal(SiloStatus.Active, entry.Status);
        }

        [Fact]
        public void MembershipEntry_Serialization_OmitsIAmAliveTime()
        {
            var first = CreateMembershipEntry(IAmAliveTime);
            var second = CreateMembershipEntry(IAmAliveTime.AddMinutes(10));

            var firstJson = Encoding.UTF8.GetString(Serialize(first));
            var secondJson = Encoding.UTF8.GetString(Serialize(second));

            Assert.Null(JObject.Parse(firstJson).Property("IAmAliveTime"));
            Assert.Equal(firstJson, secondJson);
        }

        [Fact]
        public void IAmAliveDateTime_RoundTrip_PreservesSeparatePayload()
        {
            var payload = Serialize(IAmAliveTime);
            var json = Encoding.UTF8.GetString(payload);

            Assert.Equal("\"2024-01-02T03:05:06.789Z\"", json);
            Assert.Equal(JTokenType.Date, JToken.Parse(json).Type);
            var roundTrip = Deserialize<DateTime>(payload);
            Assert.Equal(IAmAliveTime.Ticks, roundTrip.Ticks);
            Assert.Equal(DateTimeKind.Utc, roundTrip.Kind);
        }

        [Fact]
        public void SiloAddress_RoundTrip_PreservesParsableAddress()
        {
            var original = CreateSiloAddress("127.0.0.1", 11111, 12345);
            var payload = Serialize(original);
            var addressObject = JObject.Parse(Encoding.UTF8.GetString(payload));

            Assert.Equal(["SiloAddress"], addressObject.Properties().Select(property => property.Name));
            Assert.Equal("127.0.0.1:11111@12345", addressObject["SiloAddress"]!.Value<string>());

            var roundTrip = Deserialize<SiloAddress>(payload);
            Assert.Equal(IPAddress.Parse("127.0.0.1"), roundTrip.Endpoint.Address);
            Assert.Equal(11111, roundTrip.Endpoint.Port);
            Assert.Equal(12345, roundTrip.Generation);
            Assert.Equal("127.0.0.1:11111@12345", roundTrip.ToParsableString());
        }

        private static MembershipEntry CreateMembershipEntry(DateTime? iAmAliveTime = null) =>
            new()
            {
                SiloAddress = CreateSiloAddress("127.0.0.1", 11111, 12345),
                HostName = "høst-東京",
                SiloName = "silo-modern",
                Status = SiloStatus.Active,
                ProxyPort = 30000,
                StartTime = StartTime,
                IAmAliveTime = iAmAliveTime ?? IAmAliveTime,
                SuspectTimes =
                [
                    Tuple.Create(
                        CreateSiloAddress("127.0.0.2", 11112, 54321),
                        SuspectTime)
                ]
            };

        private static JObject CreateSerializedRow() =>
            JObject.Parse(Encoding.UTF8.GetString(Serialize(CreateMembershipEntry())));

        private static SiloAddress CreateSiloAddress(string address, int port, int generation) =>
            SiloAddress.New(new IPEndPoint(IPAddress.Parse(address), port), generation);

        private static byte[] Serialize(object value) =>
            ZooKeeperBasedMembershipTable.Serialize(value);

        private static T Deserialize<T>(byte[] payload) =>
            ZooKeeperBasedMembershipTable.Deserialize<T>(payload);
    }
}

using System.Globalization;
using System.Net;
using System.Reflection;
using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("AzureStorage"), TestCategory("Membership"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestArea("Membership")]
public class AzureStorageMembershipGlobalizationTests
{
    private const string ClusterId = "Cluster-Ii-'Exact";

    public static TheoryData<CultureInfo> Cultures => new()
    {
        CultureInfo.InvariantCulture,
        CultureInfo.GetCultureInfo("tr-TR"),
        CreateCultureWithNonInvariantSigns(),
    };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void RowKey_RoundTripsInvariantSchema_AcrossCultures(CultureInfo culture)
    {
        using var cultureScope = new CultureScope(culture);
        var siloAddress = SiloAddress.New(
            new IPEndPoint(IPAddress.Parse("2001:db8::1"), 22222),
            42);

        var rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);
        var unpacked = SiloInstanceTableEntry.UnpackRowKey(rowKey);

        Assert.Equal("2001:db8::1-22222-42", rowKey);
        Assert.Equal(siloAddress, unpacked);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void MembershipEntry_ConvertsToInvariantPersistedSchema_AcrossCultures(CultureInfo culture)
    {
        using var cultureScope = new CultureScope(culture);
        var membershipEntry = CreateMembershipEntry();

        var tableEntry = MembershipCodec.Convert(membershipEntry, ClusterId);

        Assert.Equal(ClusterId, tableEntry.DeploymentId, StringComparer.Ordinal);
        Assert.Equal(ClusterId, tableEntry.PartitionKey, StringComparer.Ordinal);
        Assert.Equal("2001:db8::1-22222-42", tableEntry.RowKey, StringComparer.Ordinal);
        Assert.Equal("22222", tableEntry.Port, StringComparer.Ordinal);
        Assert.Equal("42", tableEntry.Generation, StringComparer.Ordinal);
        Assert.Equal("Active", tableEntry.Status, StringComparer.Ordinal);
        Assert.Equal("30000", tableEntry.ProxyPort, StringComparer.Ordinal);
        Assert.Equal("-12", tableEntry.UpdateZone, StringComparer.Ordinal);
        Assert.Equal("34", tableEntry.FaultZone, StringComparer.Ordinal);
        Assert.Equal("2024-02-29 23:59:58.123 GMT", tableEntry.StartTime, StringComparer.Ordinal);
        Assert.Equal("2024-03-01 00:00:01.456 GMT", tableEntry.IAmAliveTime, StringComparer.Ordinal);
        Assert.Equal(
            "192.0.2.10:12345@17|2001:db8::2:23456@18",
            tableEntry.SuspectingSilos,
            StringComparer.Ordinal);
        Assert.Equal(
            "2024-02-29 23:58:57.012 GMT|2024-02-29 23:58:58.345 GMT",
            tableEntry.SuspectingTimes,
            StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void LegacyRow_RoundTripsInvariantSchema_AcrossCultures(CultureInfo culture)
    {
        using var cultureScope = new CultureScope(culture);
        var legacyRow = CreateLegacyRow();
        var originalTimestamp = legacyRow.Timestamp;
        var originalETag = legacyRow.ETag;

        var parsed = MembershipCodec.Parse(legacyRow);
        var converted = MembershipCodec.Convert(parsed, ClusterId);

        Assert.Equal(IPAddress.Parse("2001:db8::1"), parsed.SiloAddress.Endpoint.Address);
        Assert.Equal(22222, parsed.SiloAddress.Endpoint.Port);
        Assert.Equal(42, parsed.SiloAddress.Generation);
        Assert.Equal(SiloStatus.Active, parsed.Status);
        Assert.Equal(30000, parsed.ProxyPort);
        Assert.Equal(-12, parsed.UpdateZone);
        Assert.Equal(34, parsed.FaultZone);
        Assert.Equal("Legacy-Silo-Ii", parsed.SiloName, StringComparer.Ordinal);
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 58, 123), parsed.StartTime);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 1, 456), parsed.IAmAliveTime);
        Assert.Collection(
            parsed.SuspectTimes!,
            item =>
            {
                Assert.Equal("192.0.2.10:12345@17", item.Item1.ToParsableString());
                Assert.Equal(new DateTime(2024, 2, 29, 23, 58, 57, 12), item.Item2);
            },
            item =>
            {
                Assert.Equal("2001:db8::2:23456@18", item.Item1.ToParsableString());
                Assert.Equal(new DateTime(2024, 2, 29, 23, 58, 58, 345), item.Item2);
            });

        Assert.Equal("2001:db8::1-22222-42", converted.RowKey, StringComparer.Ordinal);
        Assert.Equal("-12", converted.UpdateZone, StringComparer.Ordinal);
        Assert.Equal("34", converted.FaultZone, StringComparer.Ordinal);
        Assert.Equal("2024-02-29 23:59:58.123 GMT", converted.StartTime, StringComparer.Ordinal);
        Assert.Equal("2024-03-01 00:00:01.456 GMT", converted.IAmAliveTime, StringComparer.Ordinal);

        Assert.Equal("+0007", legacyRow.MembershipVersion, StringComparer.Ordinal);
        Assert.Equal(originalTimestamp, legacyRow.Timestamp);
        Assert.Equal(originalETag, legacyRow.ETag);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void GatewayRow_ParsesInvariantPortAndGeneration_AcrossCultures(CultureInfo culture)
    {
        using var cultureScope = new CultureScope(culture);
        var gatewayRow = new SiloInstanceTableEntry
        {
            Address = "2001:db8::1",
            ProxyPort = "30000",
            Generation = "42",
        };

        var gatewayUri = MembershipCodec.ConvertToGatewayUri(gatewayRow);

        Assert.Equal("gwy.tcp://[2001:db8::1]:30000/42", gatewayUri.ToString());
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void MembershipSnapshot_AcceptsLegacyVersionAndPreservesOpaqueEtags_AcrossCultures(CultureInfo culture)
    {
        using var cultureScope = new CultureScope(culture);
        var membershipTable = new AzureBasedMembershipTable(
            NullLoggerFactory.Instance,
            Options.Create(new AzureStorageClusteringOptions()),
            Options.Create(new ClusterOptions { ClusterId = ClusterId, ServiceId = "Service" }));
        var versionRow = new SiloInstanceTableEntry
        {
            PartitionKey = ClusterId,
            RowKey = SiloInstanceTableEntry.TABLE_VERSION_ROW,
            MembershipVersion = "+0007",
            ETag = new ETag("entity-version-etag"),
        };
        var siloRow = CreateLegacyRow();

        var result = MembershipCodec.Convert(
            membershipTable,
            [(versionRow, "opaque-version-etag"), (siloRow, "opaque-silo-etag")]);

        Assert.Equal(7, result.Version.Version);
        Assert.Equal("opaque-version-etag", result.Version.VersionEtag, StringComparer.Ordinal);
        var member = Assert.Single(result.Members);
        Assert.Equal("opaque-silo-etag", member.Item2, StringComparer.Ordinal);
        Assert.Equal("Legacy-Silo-Ii", member.Item1.SiloName, StringComparer.Ordinal);
        Assert.Equal("+0007", versionRow.MembershipVersion, StringComparer.Ordinal);
        Assert.Equal("entity-version-etag", versionRow.ETag.ToString(), StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void SchemaIdentifiersAndQueries_RemainExact_AcrossCultures(CultureInfo culture)
    {
        using var cultureScope = new CultureScope(culture);
        var manager = new OrleansSiloInstanceManager(
            ClusterId,
            NullLoggerFactory.Instance,
            new AzureStorageClusteringOptions(),
            membershipTableReadStorage: null);

        var versionRow = manager.CreateTableVersionEntry(7);
        var pointQuery = AzureTableUtils.PointQuery(ClusterId, "Row-Ii-'Exact");
        var rangeQuery = AzureTableUtils.RangeQuery(
            ClusterId,
            SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN,
            SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX);

        Assert.Equal("VersionRow", versionRow.RowKey, StringComparer.Ordinal);
        Assert.Equal("7", versionRow.MembershipVersion, StringComparer.Ordinal);
        Assert.True(SiloInstanceTableEntry.IsVersionRow("VersionRow"));
        Assert.False(SiloInstanceTableEntry.IsVersionRow("versionrow"));
        var caseChangedStatus = CreateLegacyRow();
        caseChangedStatus.Status = "active";
        Assert.Throws<ArgumentException>(() => MembershipCodec.Parse(caseChangedStatus));
        Assert.Equal(
            "(PartitionKey eq 'Cluster-Ii-''Exact') and (RowKey eq 'Row-Ii-''Exact')",
            pointQuery,
            StringComparer.Ordinal);
        Assert.Equal(
            "((PartitionKey eq 'Cluster-Ii-''Exact') and (RowKey ge '!Start')) and (RowKey le '~End')",
            rangeQuery,
            StringComparer.Ordinal);
    }

    private static MembershipEntry CreateMembershipEntry() => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 22222), 42),
        HostName = "Host-Ii",
        Status = SiloStatus.Active,
        ProxyPort = 30000,
        RoleName = "Primary",
        SiloName = "Silo-Ii",
        UpdateZone = -12,
        FaultZone = 34,
        StartTime = new DateTime(2024, 2, 29, 23, 59, 58, 123, DateTimeKind.Utc),
        IAmAliveTime = new DateTime(2024, 3, 1, 0, 0, 1, 456, DateTimeKind.Utc),
        SuspectTimes =
        [
            Tuple.Create(
                SiloAddress.New(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 12345), 17),
                new DateTime(2024, 2, 29, 23, 58, 57, 12, DateTimeKind.Utc)),
            Tuple.Create(
                SiloAddress.New(new IPEndPoint(IPAddress.Parse("2001:db8::2"), 23456), 18),
                new DateTime(2024, 2, 29, 23, 58, 58, 345, DateTimeKind.Utc)),
        ],
    };

    private static SiloInstanceTableEntry CreateLegacyRow() => new()
    {
        DeploymentId = ClusterId,
        PartitionKey = ClusterId,
        RowKey = "2001:db8::1-22222-42",
        Address = "2001:db8::1",
        Port = "22222",
        Generation = "42",
        HostName = "Legacy-Host-Ii",
        Status = "Active",
        ProxyPort = "30000",
        RoleName = "Legacy-Primary",
        InstanceName = "Legacy-Silo-Ii",
        UpdateZone = "-12",
        FaultZone = "34",
        SuspectingSilos = "192.0.2.10:12345@17|2001:db8::2:23456@18",
        SuspectingTimes = "2024-02-29 23:58:57.012 GMT|2024-02-29 23:58:58.345 GMT",
        StartTime = "2024-02-29 23:59:58.123 GMT",
        IAmAliveTime = "2024-03-01 00:00:01.456 GMT",
        MembershipVersion = "+0007",
        Timestamp = new DateTimeOffset(2024, 3, 1, 0, 0, 2, 789, TimeSpan.Zero),
        ETag = new ETag("opaque-entity-etag"),
    };

    private static CultureInfo CreateCultureWithNonInvariantSigns()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.PositiveSign = "!";
        return culture;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUICulture = CultureInfo.CurrentUICulture;

        public CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    private static class MembershipCodec
    {
        private static readonly Type CodecType = typeof(AzureBasedMembershipTable);
        private static readonly MethodInfo ConvertEntryMethod = BindStatic(
            "Convert",
            typeof(SiloInstanceTableEntry),
            typeof(MembershipEntry),
            typeof(string));
        private static readonly MethodInfo ParseMethod = BindStatic(
            "Parse",
            typeof(MembershipEntry),
            typeof(SiloInstanceTableEntry));
        private static readonly MethodInfo ConvertEntriesMethod = CodecType.GetMethod(
            "Convert",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(List<(SiloInstanceTableEntry Entity, string ETag)>)],
            modifiers: null)
            ?? throw new MissingMethodException(CodecType.FullName, "Convert");
        private static readonly MethodInfo ConvertToGatewayUriMethod =
            typeof(OrleansSiloInstanceManager).GetMethod(
                "ConvertToGatewayUri",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(SiloInstanceTableEntry)],
                modifiers: null)
            ?? throw new MissingMethodException(
                typeof(OrleansSiloInstanceManager).FullName,
                "ConvertToGatewayUri");

        public static SiloInstanceTableEntry Convert(MembershipEntry entry, string deploymentId)
            => Invoke<SiloInstanceTableEntry>(ConvertEntryMethod, null, entry, deploymentId);

        public static MembershipEntry Parse(SiloInstanceTableEntry entity)
            => Invoke<MembershipEntry>(ParseMethod, null, entity);

        public static MembershipTableData Convert(
            AzureBasedMembershipTable table,
            List<(SiloInstanceTableEntry Entity, string ETag)> entries)
            => Invoke<MembershipTableData>(ConvertEntriesMethod, table, entries);

        public static Uri ConvertToGatewayUri(SiloInstanceTableEntry gatewayRow)
            => Invoke<Uri>(ConvertToGatewayUriMethod, null, gatewayRow);

        private static MethodInfo BindStatic(string name, Type returnType, params Type[] parameterTypes)
        {
            var method = CodecType.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: parameterTypes,
                modifiers: null);
            return method is { ReturnType: var actualReturnType } && actualReturnType == returnType
                ? method
                : throw new MissingMethodException(CodecType.FullName, name);
        }

        private static TResult Invoke<TResult>(MethodInfo method, object? instance, params object?[] arguments)
        {
            try
            {
                return (TResult)method.Invoke(instance, arguments)!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Xunit;

namespace Orleans.GrainDirectory.Firestore.Tests;

[TestSuite("BVT")]
[TestProvider("GoogleCloud")]
[TestArea("GrainDirectory")]
[TestCategory("GrainDirectory"), TestCategory("BVT"), TestCategory("Firestore"), TestCategory("GoogleCloud")]
public class FirestoreGrainDirectoryNullTests
{
    [Fact]
    public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new FirestoreGrainDirectory(
            null!,
            CreateFirestoreOptions(),
            NullLoggerFactory.Instance));

        Assert.Equal("clusterOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullFirestoreOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new FirestoreGrainDirectory(
            CreateClusterOptions(),
            null!,
            NullLoggerFactory.Instance));

        Assert.Equal("firestoreOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullLoggerFactory_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new FirestoreGrainDirectory(
            CreateClusterOptions(),
            CreateFirestoreOptions(),
            null!));

        Assert.Equal("loggerFactory", exception.ParamName);
    }

    [Fact]
    public async Task Register_NullAddress_ThrowsArgumentNullException()
    {
        var directory = CreateGrainDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => directory.Register(null!));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task RegisterWithCancellation_NullAddress_ThrowsArgumentNullException()
    {
        IGrainDirectory directory = CreateGrainDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Register(null!, TestContext.Current.CancellationToken));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task RegisterWithPreviousAddress_NullAddress_ThrowsArgumentNullException()
    {
        IGrainDirectory directory = CreateGrainDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Register(null!, previousAddress: null, TestContext.Current.CancellationToken));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task RegisterWithoutCancellation_NullAddress_ThrowsArgumentNullException()
    {
        IGrainDirectory directory = CreateGrainDirectory();
        var previousAddress = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/previous"),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51),
        };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RegisterWithoutCancellation(directory, null!, previousAddress));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task Unregister_NullAddress_ThrowsArgumentNullException()
    {
        var directory = CreateGrainDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => directory.Unregister(null!));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task UnregisterWithCancellation_NullAddress_ThrowsArgumentNullException()
    {
        IGrainDirectory directory = CreateGrainDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Unregister(null!, TestContext.Current.CancellationToken));

        Assert.Equal("address", exception.ParamName);
    }

    private static FirestoreGrainDirectory CreateGrainDirectory() =>
        new(CreateClusterOptions(), CreateFirestoreOptions(), NullLoggerFactory.Instance);

    private static Task<GrainAddress?> RegisterWithoutCancellation(
        IGrainDirectory directory,
        GrainAddress address,
        GrainAddress? previousAddress) =>
        directory.Register(address, previousAddress);

    private static IOptions<ClusterOptions> CreateClusterOptions() =>
        Options.Create(new ClusterOptions
        {
            ClusterId = "cluster-id",
            ServiceId = "service-id",
        });

    private static IOptions<FirestoreOptions> CreateFirestoreOptions() =>
        Options.Create(new FirestoreOptions
        {
            ProjectId = "project-id",
            EmulatorHost = "localhost:8080",
        });
}

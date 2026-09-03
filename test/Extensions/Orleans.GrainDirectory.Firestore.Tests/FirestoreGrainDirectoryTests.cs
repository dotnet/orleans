using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Tester.Directories;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.GrainDirectory.Firestore;
using Xunit;

namespace Orleans.GrainDirectory.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestArea("GrainDirectory")]
[TestCategory("GrainDirectory"), TestCategory("Functional"), TestCategory("Firestore"),
 TestCategory("GoogleCloud")]
public class FirestoreGrainDirectoryTests : GrainDirectoryTests<FirestoreGrainDirectory>, IAsyncLifetime
{
    private FirestoreGrainDirectory? _grainDirectory;

    public FirestoreGrainDirectoryTests(ITestOutputHelper testOutput) : base(testOutput)
    {
    }

    protected override FirestoreGrainDirectory CreateGrainDirectory() =>
        _grainDirectory ?? throw new InvalidOperationException("The grain directory has not been initialized.");

    [Fact]
    public async Task UnregisterSilosSupportsMoreThanOneQueryBatch()
    {
        const int count = 12;
        var addresses = Enumerable.Range(0, count).Select(i => new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse($"user/{Guid.NewGuid():N}"),
            SiloAddress = SiloAddress.FromParsableString($"10.0.23.12:{1000 + i}@{5678 + i}"),
            MembershipVersion = new MembershipVersion(51),
        }).ToArray();

        foreach (var address in addresses)
        {
            await GrainDirectory.Register(address);
        }

        await GrainDirectory.UnregisterSilos(addresses.Select(address => address.SiloAddress!).ToList());

        foreach (var address in addresses)
        {
            Assert.Null(await GrainDirectory.Lookup(address.GrainId));
        }
    }

    [Fact]
    public async Task UnregisterSilosToleratesConcurrentCleanup()
    {
        var address = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse($"user/{Guid.NewGuid():N}"),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51),
        };
        await GrainDirectory.Register(address);
        var silos = new List<SiloAddress> { address.SiloAddress };

        await Task.WhenAll(
            GrainDirectory.UnregisterSilos(silos),
            GrainDirectory.UnregisterSilos(silos));

        Assert.Null(await GrainDirectory.Lookup(address.GrainId));
    }

    public async ValueTask InitializeAsync()
    {
        var clusterOptions = new ClusterOptions
        {
            ClusterId = Guid.NewGuid().ToString("N"),
            ServiceId = Guid.NewGuid().ToString("N"),
        };

        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        _grainDirectory =
            new FirestoreGrainDirectory(Options.Create(clusterOptions), Options.Create(options), loggerFactory);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        _grainDirectory.Participate(lifecycle);
        await lifecycle.OnStart();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

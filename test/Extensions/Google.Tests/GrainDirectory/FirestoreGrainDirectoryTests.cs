using Microsoft.Extensions.Options;
using Tester.Directories;
using Xunit.Abstractions;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.GrainDirectory.GoogleFirestore;

namespace Orleans.Tests.Google;

[TestCategory("GrainDirectory"), TestCategory("Functional"), TestCategory("GoogleFirestore"),
 TestCategory("GoogleCloud")]
public class FirestoreGrainDirectoryTests : GrainDirectoryTests<GoogleFirestoreGrainDirectory>, IAsyncLifetime
{
    public FirestoreGrainDirectoryTests(ITestOutputHelper testOutput) : base(testOutput)
    {
    }

    // Dummy implementation, will get the directory from IAsyncLifetime.InitializeAsync();
    protected override GoogleFirestoreGrainDirectory GetGrainDirectory() => default!;

    [SkippableFact]
    public async Task UnregisterMany()
    {
        const int N = 25;
        const int R = 4;

        // Create and insert N entries
        var addresses = new List<GrainAddress>();
        for (var i = 0; i < N; i++)
        {
            var addr = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };
            addresses.Add(addr);
            await this.grainDirectory.Register(addr);
        }

        // Modify the Rth entry locally, to simulate another activation tentative by another silo
        var ra = addresses[R];
        var oldActivation = ra.ActivationId;
        addresses[R] = new()
        {
            GrainId = ra.GrainId,
            SiloAddress = ra.SiloAddress,
            MembershipVersion = ra.MembershipVersion,
            ActivationId = ActivationId.NewId()
        };

        // Batch unregister
        await this.grainDirectory.UnregisterMany(addresses);

        // Now we should only find the old Rth entry
        for (int i = 0; i < N; i++)
        {
            if (i == R)
            {
                var addr = await this.grainDirectory.Lookup(addresses[i].GrainId);
                Assert.NotNull(addr);
                Assert.Equal(oldActivation, addr.ActivationId);
            }
            else
            {
                Assert.Null(await this.grainDirectory.Lookup(addresses[i].GrainId));
            }
        }
    }

    [SkippableFact]
    public void ConversionTest()
    {
        var addr = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(806)
        };
        var entity = GoogleFirestoreGrainDirectory.ConvertToEntity(addr);
        Assert.Equal(addr, GoogleFirestoreGrainDirectory.GetGrainAddress(entity));
    }

    public async Task InitializeAsync()
    {
        var clusterOptions = new ClusterOptions
        {
            ClusterId = Guid.NewGuid().ToString("N"), ServiceId = Guid.NewGuid().ToString("N"),
        };

        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId, EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        this.grainDirectory =
            new GoogleFirestoreGrainDirectory(Options.Create(clusterOptions), Options.Create(options), loggerFactory);
        await this.grainDirectory.Init();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
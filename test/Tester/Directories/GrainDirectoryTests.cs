using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Directories;

internal interface IMyDirectoryTestGrain : IGrainWithGuidKey
{
    ValueTask<string> Echo(string data);
}

internal class MyDirectoryTestGrain : Grain, IMyDirectoryTestGrain
{
    public ValueTask<string> Echo(string data) => new(data);
}

public sealed class ReplicatedGrainDirectoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DynamicClusterTest()
    {
        var testClusterBuilder = new TestClusterBuilder(1);
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var reconfigurationTimer = CoarseStopwatch.StartNew();
        var target = 10;
        var upperLimit = 10;
        var lowerLimit = 1;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5));
                try
                {
                    for (var i = 0; i < 100; i++)
                    {
                        await testCluster.GrainFactory.GetGrain<IMyDirectoryTestGrain>(Guid.NewGuid()).Echo("hello");
                    }

                    if (reconfigurationTimer.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        reconfigurationTimer.Restart();
                        var currentCount = testCluster.Silos.Count;

                        if (currentCount > target)
                        {
                            var victim = testCluster.Silos.Last();
                            if (currentCount % 2 == 0)
                            {
                                await testCluster.StopSiloAsync(victim);
                            }
                            else
                            {
                                await testCluster.KillSiloAsync(victim);
                            }
                        }
                        else if (currentCount < target)
                        {
                            await testCluster.StartAdditionalSiloAsync();
                        }

                        if (currentCount <= lowerLimit)
                        {
                            target = upperLimit;
                        }
                        else if (currentCount >= upperLimit)
                        {
                            target = lowerLimit;
                        }
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine($"Exception: {exception}");
                }
            }
        }
        finally
        {
            await testCluster.StopAllSilosAsync();
            await testCluster.DisposeAsync();
        }
    }
}

// Base tests for custom Grain Directory
public abstract class GrainDirectoryTests<T> where T : IGrainDirectory
{
    protected T grainDirectory;
    protected readonly ILoggerFactory loggerFactory;

    protected GrainDirectoryTests(ITestOutputHelper testOutput)
    {
        this.loggerFactory = new LoggerFactory();
        this.loggerFactory.AddProvider(new XunitLoggerProvider(testOutput));
        this.grainDirectory = GetGrainDirectory();
    }

    protected abstract T GetGrainDirectory();

    [SkippableFact]
    public async Task RegisterLookupUnregisterLookup()
    {
        var expected = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        Assert.Equal(expected, await this.grainDirectory.Register(expected, null));

        Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));

        await this.grainDirectory.Unregister(expected);

        Assert.Null(await this.grainDirectory.Lookup(expected.GrainId));
    }

    [SkippableFact]
    public async Task DoNotOverwriteEntry()
    {
        var expected = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var differentActivation = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = expected.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var differentSilo = new GrainAddress
        {
            ActivationId = expected.ActivationId,
            GrainId = expected.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583"),
            MembershipVersion = new MembershipVersion(51)
        };

        Assert.Equal(expected, await this.grainDirectory.Register(expected, null));
        Assert.Equal(expected, await this.grainDirectory.Register(differentActivation, null));
        Assert.Equal(expected, await this.grainDirectory.Register(differentSilo, null));

        Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));
    }

    /// <summary>
    /// Overwrite an existing entry if the register call includes a matching "previousAddress" parameter.
    /// </summary>
    [SkippableFact]
    public async Task OverwriteEntryIfMatch()
    {
        var initial = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var differentActivation = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = initial.GrainId,
            SiloAddress = initial.SiloAddress,
            MembershipVersion = initial.MembershipVersion
        };

        var differentSilo = new GrainAddress
        {
            ActivationId = initial.ActivationId,
            GrainId = initial.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583"),
            MembershipVersion = initial.MembershipVersion
        };

        // Success, no registration exists, so the previous address is ignored.
        Assert.Equal(initial, await this.grainDirectory.Register(initial, differentSilo));

        // Success, the previous address matches the existing registration.
        Assert.Equal(differentActivation, await this.grainDirectory.Register(differentActivation, initial));

        // Failure, the previous address does not match the existing registration.
        Assert.Equal(differentActivation, await this.grainDirectory.Register(differentSilo, initial));

        Assert.Equal(differentActivation, await this.grainDirectory.Lookup(initial.GrainId));
    }

    [SkippableFact]
    public async Task DoNotDeleteDifferentActivationIdEntry()
    {
        var expected = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var otherEntry = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = expected.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        Assert.Equal(expected, await this.grainDirectory.Register(expected, null));
        await this.grainDirectory.Unregister(otherEntry);
        Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));
    }

    [SkippableFact]
    public async Task LookupNotFound()
    {
        Assert.Null(await this.grainDirectory.Lookup(GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N"))));
    }
}

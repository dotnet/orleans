#nullable enable
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

/// <summary>
/// Grain interface for migration tests that reports its silo address.
/// </summary>
internal interface IMigrationTestGrain : IGrainWithIntegerKey
{
    /// <summary>Ping the grain to ensure it's activated.</summary>
    ValueTask Ping();

    /// <summary>Gets the silo address where this grain is activated.</summary>
    Task<SiloAddress> GetSiloAddress();

    /// <summary>Gets the unique activation ID to detect duplicate activations.</summary>
    Task<Guid> GetActivationId();
}

/// <summary>
/// Grain interface with preferred silo placement for forcing activation on specific silos.
/// </summary>
internal interface IPlacedMigrationTestGrain : IGrainWithIntegerKey
{
    ValueTask Ping();
    Task<SiloAddress> GetSiloAddress();
    Task<Guid> GetActivationId();
}

/// <summary>
/// Implementation for migration tests.
/// </summary>
[CollectionAgeLimit(Minutes = 10)]
internal class MigrationTestGrain(ILocalSiloDetails localSiloDetails) : Grain, IMigrationTestGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public ValueTask Ping() => default;

    public Task<SiloAddress> GetSiloAddress() => Task.FromResult(localSiloDetails.SiloAddress);

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}

/// <summary>
/// Grain with PreferLocalPlacement to help control where activations land.
/// </summary>
[PreferLocalPlacement]
[CollectionAgeLimit(Minutes = 10)]
internal class PlacedMigrationTestGrain(ILocalSiloDetails localSiloDetails) : Grain, IPlacedMigrationTestGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public ValueTask Ping() => default;

    public Task<SiloAddress> GetSiloAddress() => Task.FromResult(localSiloDetails.SiloAddress);

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}

/// <summary>
/// Tests for rolling upgrades from LocalGrainDirectory to DistributedGrainDirectory.
/// </summary>
/// <remarks>
/// <para>
/// These tests verify that a cluster can be migrated from the legacy DHT-based
/// LocalGrainDirectory to the new DistributedGrainDirectory.
/// </para>
/// <para>
/// Migration Invariants:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>DURING migration (mixed cluster)</term>
///     <description>
///       AVAILABILITY is the priority. Grains MUST be accessible. Temporary duplicate
///       activations MAY occur and are acceptable during this phase.
///     </description>
///   </item>
///   <item>
///     <term>AFTER migration (NEW-only cluster)</term>
///     <description>
///       CONSISTENCY is the priority. There MUST be NO duplicate activations and
///       NO orphaned activations. All grains must be accessible with exactly one activation.
///     </description>
///   </item>
/// </list>
/// </remarks>
[TestCategory("SlowBVT"), TestCategory("Directory")]
public sealed class GrainDirectoryMigrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly HashSet<SiloAddress> _newSilos = [];
    private readonly List<SiloHandle> _newSiloHandles = [];
    private InProcessTestCluster _testCluster = null!;
    private ILogger _log = null!;

    /// <summary>
    /// Tracks which silo instance numbers should use DistributedGrainDirectory (NEW silos).
    /// Instance numbers 0 and 1 are OLD silos (using LocalGrainDirectory).
    /// </summary>
    private const int FirstNewSiloInstance = 2;

    public async Task InitializeAsync()
    {
        // Start with OLD silos using legacy LocalGrainDirectory (default)
        var builder = new InProcessTestClusterBuilder(2);

        // Disable the shared in-memory grain directory so each silo uses its own grain directory
        builder.Options.UseTestClusterGrainDirectory = false;

        builder.ConfigureSilo((options, siloBuilder) =>
        {
            siloBuilder.Configure<SiloMessagingOptions>(o =>
            {
                o.ResponseTimeout = TimeSpan.FromMinutes(2);
                o.SystemResponseTimeout = TimeSpan.FromMinutes(2);
            });

            // Determine if this silo should use DistributedGrainDirectory based on its name
            // Silo_0 and Silo_1 are OLD silos, Silo_2+ are NEW silos
            var siloNumber = int.Parse(options.SiloName.Split('_')[1], CultureInfo.InvariantCulture);
            if (siloNumber >= FirstNewSiloInstance)
            {
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
            }
        });

        _testCluster = builder.Build();
        await _testCluster.DeployAsync();
        _log = _testCluster.GetSiloServiceProvider(_testCluster.Silos.First().SiloAddress)
            .GetRequiredService<ILogger<GrainDirectoryMigrationTests>>();
        _log.LogInformation("Test cluster deployed with {SiloCount} OLD silos", _testCluster.Silos.Count);
    }

    public async Task DisposeAsync()
    {
        await _testCluster.StopSilosAsync();
        await _testCluster.DisposeAsync();
    }

    #region During Migration: AVAILABILITY Tests

    /// <summary>
    /// Invariant: During migration, grains activated on OLD silos MUST remain accessible
    /// when NEW silos join the cluster.
    /// </summary>
    [Fact]
    public async Task DuringMigration_GrainsOnOldSilos_RemainAccessible()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var cancellationToken = cts.Token;

        // Arrange: Activate grains on OLD silos
        const int grainCount = 20;
        var grainActivations = new Dictionary<long, (SiloAddress Silo, Guid ActivationId)>();

        for (var i = 0; i < grainCount; i++)
        {
            var grain = _testCluster.Client.GetGrain<IMigrationTestGrain>(i);
            await grain.Ping();
            grainActivations[i] = (await grain.GetSiloAddress(), await grain.GetActivationId());
        }

        _log.LogInformation("Created {Count} grains on OLD silos", grainCount);

        // Act: Add NEW silos to create a mixed cluster
        var newSilo1 = await StartNewSiloAsync();
        var newSilo2 = await StartNewSiloAsync();
        await WaitForClusterMembershipAsync(cancellationToken);

        _log.LogInformation("Mixed cluster: 2 OLD + 2 NEW silos");

        // Assert: All grains MUST be accessible (AVAILABILITY)
        var accessFailures = await CheckGrainsAccessible(Enumerable.Range(0, grainCount));
        Assert.Empty(accessFailures);

        _log.LogInformation("AVAILABILITY verified: All {Count} grains accessible in mixed cluster", grainCount);
    }

    /// <summary>
    /// Invariant: During migration, NEW grains activated in mixed cluster MUST be accessible
    /// from both OLD and NEW silos.
    /// </summary>
    [Fact]
    public async Task DuringMigration_NewGrains_AccessibleFromAllSilos()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var cancellationToken = cts.Token;

        // Arrange: Create mixed cluster
        var newSilo1 = await StartNewSiloAsync();
        var newSilo2 = await StartNewSiloAsync();
        await WaitForClusterMembershipAsync(cancellationToken);

        // Get grain factories from all silos
        var factories = _testCluster.Silos
            .Cast<InProcessSiloHandle>()
            .Select(h => (h.SiloAddress, IsNew: IsNewSilo(h), Factory: h.SiloHost.Services.GetRequiredService<IGrainFactory>()))
            .ToList();

        // Act: Create grains and access from all silos
        const int grainCount = 30;
        for (var i = 0; i < grainCount; i++)
        {
            var grain = _testCluster.Client.GetGrain<IMigrationTestGrain>(i);
            await grain.Ping();
        }

        // Assert: All grains accessible from every silo (AVAILABILITY)
        foreach (var (siloAddress, isNew, factory) in factories)
        {
            var siloType = isNew ? "NEW" : "OLD";
            var failures = new List<int>();

            for (var i = 0; i < grainCount; i++)
            {
                try
                {
                    var grain = factory.GetGrain<IMigrationTestGrain>(i);
                    await grain.Ping();
                }
                catch
                {
                    failures.Add(i);
                }
            }

            Assert.Empty(failures);
            _log.LogInformation("AVAILABILITY verified: All grains accessible from {SiloType} silo {Silo}", siloType, siloAddress);
        }
    }

    /// <summary>
    /// Invariant: During migration, grains activated on NEW silos MUST be accessible
    /// from OLD silos (cross-directory lookup).
    /// </summary>
    [Fact]
    public async Task DuringMigration_GrainsOnNewSilo_AccessibleFromOldSilo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var cancellationToken = cts.Token;

        // Arrange: Start NEW silo
        var newSilo = await StartNewSiloAsync();
        await WaitForClusterMembershipAsync(cancellationToken);

        var newSiloHandle = (InProcessSiloHandle)newSilo;
        var newSiloGrainFactory = newSiloHandle.SiloHost.Services.GetRequiredService<IGrainFactory>();

        var oldSiloHandle = _testCluster.Silos.First(s => !IsNewSilo(s));
        var oldSiloGrainFactory = oldSiloHandle.SiloHost.Services.GetRequiredService<IGrainFactory>();

        // Act: Activate grain from NEW silo (PreferLocalPlacement should place it there)
        var grain = newSiloGrainFactory.GetGrain<IPlacedMigrationTestGrain>(100);
        await grain.Ping();
        var activationSilo = await grain.GetSiloAddress();
        var activationId = await grain.GetActivationId();

        Assert.Equal(newSilo.SiloAddress, activationSilo);
        _log.LogInformation("Grain activated on NEW silo {Silo}", activationSilo);

        // Assert: Grain MUST be accessible from OLD silo (AVAILABILITY)
        var grainFromOldSilo = oldSiloGrainFactory.GetGrain<IPlacedMigrationTestGrain>(100);
        var exception = await Record.ExceptionAsync(async () => await grainFromOldSilo.Ping());
        Assert.Null(exception);

        _log.LogInformation("AVAILABILITY verified: Grain on NEW silo accessible from OLD silo");
    }

    #endregion

    #region After Migration: CONSISTENCY Tests
    // NOTE: These tests document the CONSISTENCY invariants that must hold AFTER migration completes.
    // These invariants are only valid to check AFTER the upgrade has completed (all hosts running new directory version).
    // During migration, duplicates may occur (AVAILABILITY > CONSISTENCY), but after migration completes,
    // the directory must maintain strict consistency (no duplicates, no orphans).

    /// <summary>
    /// Invariant: After migration, grains activated on NEW silos during migration MUST remain accessible,
    /// new grains can be activated, and directory integrity MUST be maintained.
    /// </summary>
    /// <remarks>
    /// This test verifies the complete post-migration scenario:
    /// 1. Grains placed on NEW silos DURING migration survive when OLD silos are removed
    /// 2. New grains can be activated after migration completes
    /// 3. Directory integrity is maintained (no orphans, no duplicates)
    /// 
    /// Note: Grains on OLD silos are expected to be deactivated when those silos shut down.
    /// </remarks>
    [Fact]
    public async Task AfterMigration_GrainsOnNewSilos_SurviveMigration_AndIntegrityMaintained()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var cancellationToken = cts.Token;

        // Arrange: Start NEW silos to create mixed cluster
        var newSilos = new List<SiloHandle>();
        for (var i = 0; i < 3; i++)
        {
            newSilos.Add(await StartNewSiloAsync());
        }
        await WaitForClusterMembershipAsync(cancellationToken);

        // Get grain factories from NEW silos to place grains there using PreferLocalPlacement
        var newSiloFactories = newSilos
            .Cast<InProcessSiloHandle>()
            .Select(h => (h.SiloAddress, Factory: h.SiloHost.Services.GetRequiredService<IGrainFactory>()))
            .ToList();

        // Create grains on NEW silos DURING migration (while OLD silos still exist)
        // Use IPlacedMigrationTestGrain which has PreferLocalPlacement attribute
        const int grainsPerNewSilo = 10;
        var grainsDuringMigration = new List<(int GrainId, SiloAddress ExpectedSilo)>();
        var grainId = 0;

        foreach (var (siloAddress, factory) in newSiloFactories)
        {
            for (var i = 0; i < grainsPerNewSilo; i++)
            {
                var grain = factory.GetGrain<IPlacedMigrationTestGrain>(grainId);
                await grain.Ping();
                var actualSilo = await grain.GetSiloAddress();
                Assert.Equal(siloAddress, actualSilo); // Verify placement worked
                grainsDuringMigration.Add((grainId, siloAddress));
                grainId++;
            }
        }

        _log.LogInformation(
            "Created {Count} grains on NEW silos DURING migration: [{Grains}]",
            grainsDuringMigration.Count,
            string.Join(", ", grainsDuringMigration.Select(g => $"{g.GrainId}@{g.ExpectedSilo}")));

        // Capture OLD silos before stopping them
        var oldSilos = _testCluster.Silos.Where(s => !IsNewSilo(s)).ToList();
        var oldSiloAddresses = oldSilos.Select(s => s.SiloAddress).ToImmutableHashSet();
        _log.LogInformation("OLD silos to stop: [{OldSilos}]", string.Join(", ", oldSiloAddresses));

        // Act: Stop OLD silos to complete migration
        foreach (var oldSilo in oldSilos)
        {
            _log.LogInformation("Stopping OLD silo {Silo}", oldSilo.SiloAddress);
            await _testCluster.StopSiloAsync(oldSilo);
        }
        await WaitForClusterMembershipAsync(cancellationToken);

        // Wait for DirectoryMembershipService on all NEW silos to no longer include any OLD silos
        await WaitForDirectoryMembershipToExcludeSilosAsync(newSilos, oldSiloAddresses);

        _log.LogInformation("Migration complete - OLD silos removed, only NEW silos remain");

        // Assert 1: Grains created on NEW silos DURING migration must still be accessible
        // AND must still be on the same silo (not re-activated elsewhere)
        var survivorFailures = new List<(int GrainId, string Error)>();
        var newSiloHandle = (InProcessSiloHandle)newSilos.First();
        var grainFactory = newSiloHandle.SiloHost.Services.GetRequiredService<IGrainFactory>();

        foreach (var (gid, expectedSilo) in grainsDuringMigration)
        {
            try
            {
                var grain = grainFactory.GetGrain<IPlacedMigrationTestGrain>(gid);
                await grain.Ping();
                var actualSilo = await grain.GetSiloAddress();
                if (actualSilo != expectedSilo)
                {
                    survivorFailures.Add((gid, $"Grain moved from {expectedSilo} to {actualSilo}"));
                }
            }
            catch (Exception ex)
            {
                survivorFailures.Add((gid, $"Not accessible: {ex.Message}"));
            }
        }

        if (survivorFailures.Count > 0)
        {
            foreach (var (gid, error) in survivorFailures.Take(5))
            {
                _log.LogError("SURVIVOR FAILURE: Grain {GrainId}: {Error}", gid, error);
            }
        }

        Assert.Empty(survivorFailures);
        _log.LogInformation(
            "CONSISTENCY verified: All {Count} grains created during migration survived on their original silos",
            grainsDuringMigration.Count);

        // Assert 2: New grains can be activated after migration completes
        const int grainsAfterMigration = 20;
        var postMigrationGrainIds = Enumerable.Range(1000, grainsAfterMigration).ToList();
        var postMigrationFailures = new List<(int GrainId, Exception Error)>();

        foreach (var gid in postMigrationGrainIds)
        {
            try
            {
                var grain = grainFactory.GetGrain<IMigrationTestGrain>(gid);
                await grain.Ping();
            }
            catch (Exception ex)
            {
                postMigrationFailures.Add((gid, ex));
            }
        }

        Assert.Empty(postMigrationFailures);
        _log.LogInformation(
            "CONSISTENCY verified: {Count} new grains successfully activated after migration",
            grainsAfterMigration);

        // Assert 3: Directory integrity is maintained across all partitions
        var client = newSiloHandle.SiloHost.Services.GetRequiredService<IInternalGrainFactory>();
        var integrityErrors = new List<(SiloAddress Silo, int Partition, Exception Error)>();

        foreach (var silo in newSilos)
        {
            var address = silo.SiloAddress;
            for (var partitionIndex = 0; partitionIndex < DirectoryMembershipSnapshot.PartitionsPerSilo; partitionIndex++)
            {
                try
                {
                    var replica = client.GetSystemTarget<IGrainDirectoryTestHooks>(
                        GrainDirectoryPartition.CreateGrainId(address, partitionIndex).GrainId);
                    await replica.CheckIntegrityAsync();
                }
                catch (Exception ex)
                {
                    integrityErrors.Add((address, partitionIndex, ex));
                }
            }
        }

        if (integrityErrors.Count > 0)
        {
            foreach (var (silo, partition, error) in integrityErrors)
            {
                _log.LogError(error, "INTEGRITY VIOLATION: Partition {Partition} on {Silo} failed", partition, silo);
            }
        }

        Assert.Empty(integrityErrors);
        _log.LogInformation(
            "CONSISTENCY verified: Directory integrity maintained across all {Count} partitions",
            newSilos.Count * DirectoryMembershipSnapshot.PartitionsPerSilo);
    }

    #endregion

    #region Helper Methods

    private async Task<SiloHandle> StartNewSiloAsync()
    {
        // With InProcessTestCluster, the ConfigureSilo callback in InitializeAsync() automatically
        // configures silos with instance number >= FirstNewSiloInstance to use DistributedGrainDirectory.
        // We just need to start a new silo with the next instance number.
        var instanceNumber = _testCluster.Silos.Count + _newSiloHandles.Count;
        var handle = await _testCluster.StartSiloAsync(instanceNumber, _testCluster.Options);

        _newSilos.Add(handle.SiloAddress);
        _newSiloHandles.Add(handle);

        _log.LogInformation("Started NEW silo (instance {Instance}): {Address}", instanceNumber, handle.SiloAddress);
        return handle;
    }

    private bool IsNewSilo(SiloHandle silo) => _newSilos.Contains(silo.SiloAddress);

    /// <summary>
    /// Gets all active silos including those started manually (not tracked by InProcessTestCluster).
    /// </summary>
    private IEnumerable<SiloHandle> GetAllActiveSilos()
    {
        foreach (var silo in _testCluster.Silos)
        {
            if (silo.IsActive)
                yield return silo;
        }
        foreach (var silo in _newSiloHandles)
        {
            if (silo.IsActive)
                yield return silo;
        }
    }

    private async Task<List<(int GrainId, Exception Error)>> CheckGrainsAccessible(IEnumerable<int> grainIds)
    {
        var failures = new List<(int GrainId, Exception Error)>();

        foreach (var id in grainIds)
        {
            try
            {
                var grain = _testCluster.Client.GetGrain<IMigrationTestGrain>(id);
                await grain.Ping();
            }
            catch (Exception ex)
            {
                failures.Add((id, ex));
            }
        }

        return failures;
    }

    /// <summary>
    /// Waits for DirectoryMembershipService on all specified silos to no longer include any of the specified silos.
    /// This is useful for waiting until old silos have been fully removed from the directory.
    /// </summary>
    private async Task WaitForDirectoryMembershipToExcludeSilosAsync(
        IEnumerable<SiloHandle> silosToCheck,
        ImmutableHashSet<SiloAddress> silosToExclude,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout.Value);

        var siloList = silosToCheck.ToList();
        _log.LogInformation(
            "Waiting for {Count} silos' DirectoryMembershipService to exclude silos: [{Excluded}]",
            siloList.Count,
            string.Join(", ", silosToExclude));

        var tasks = siloList.Select(async silo =>
        {
            var handle = (InProcessSiloHandle)silo;
            var directoryMembershipService = handle.SiloHost.Services.GetService<DirectoryMembershipService>();

            if (directoryMembershipService == null)
            {
                // OLD silo without DistributedGrainDirectory - skip
                _log.LogInformation("Silo {Silo} does not have DirectoryMembershipService (OLD silo), skipping", silo.SiloAddress);
                return;
            }

            // Check if current view already excludes the silos
            bool HasExcludedSilos(DirectoryMembershipSnapshot view) =>
                view.Members.Any(m => silosToExclude.Contains(m));

            if (!HasExcludedSilos(directoryMembershipService.CurrentView))
            {
                _log.LogInformation(
                    "Silo {Silo} DirectoryMembership already excludes all old silos (version {Version}), members: [{Members}]",
                    silo.SiloAddress,
                    directoryMembershipService.CurrentView.Version,
                    string.Join(", ", directoryMembershipService.CurrentView.Members));
                return;
            }

            // Wait for view updates
            await foreach (var view in directoryMembershipService.ViewUpdates.WithCancellation(cts.Token))
            {
                if (!HasExcludedSilos(view))
                {
                    _log.LogInformation(
                        "Silo {Silo} DirectoryMembership now excludes all old silos (version {Version}), members: [{Members}]",
                        silo.SiloAddress,
                        view.Version,
                        string.Join(", ", view.Members));
                    return;
                }
                else
                {
                    _log.LogInformation(
                        "Silo {Silo} DirectoryMembership updated to version {Version}, still has old silos, members: [{Members}]",
                        silo.SiloAddress,
                        view.Version,
                        string.Join(", ", view.Members));
                }
            }
        });

        try
        {
            await Task.WhenAll(tasks);
            _log.LogInformation("All silos' DirectoryMembershipService now exclude the old silos");
        }
        catch (OperationCanceledException)
        {
            foreach (var silo in siloList)
            {
                var handle = (InProcessSiloHandle)silo;
                var directoryMembershipService = handle.SiloHost.Services.GetService<DirectoryMembershipService>();
                if (directoryMembershipService != null)
                {
                    var remainingOldSilos = directoryMembershipService.CurrentView.Members
                        .Where(silosToExclude.Contains)
                        .ToList();
                    _log.LogError(
                        "TIMEOUT: Silo {Silo} DirectoryMembership still has old silos [{OldSilos}] in members [{Members}]",
                        silo.SiloAddress,
                        string.Join(", ", remainingOldSilos),
                        string.Join(", ", directoryMembershipService.CurrentView.Members));
                }
            }
            throw new TimeoutException($"DirectoryMembershipService still contains old silos after {timeout}");
        }
    }

    /// <summary>
    /// Waits for all silos in the cluster to converge on the same view of active membership.
    /// This is a faster alternative to WaitForLivenessToStabilizeAsync that uses polling.
    /// </summary>
    private async Task WaitForClusterMembershipAsync(CancellationToken cancellationToken)
    {
        output.WriteLine($"WaitForClusterMembershipAsync: Waiting for cluster membership to converge");

        var logInterval = TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();
        var lastLogTime = stopwatch.Elapsed;

        while (!cancellationToken.IsCancellationRequested)
        {
            var allActiveSilos = GetAllActiveSilos().ToList();
            var expectedAddresses = allActiveSilos.Select(s => s.SiloAddress).ToImmutableHashSet();

            // Get the current membership from each silo and check if they all agree
            var siloViews = new List<(SiloAddress Silo, MembershipVersion Version, ImmutableHashSet<SiloAddress> ActiveMembers)>();

            foreach (var silo in allActiveSilos)
            {
                var handle = (InProcessSiloHandle)silo;
                var cms = handle.SiloHost.Services.GetRequiredService<IClusterMembershipService>();
                var snapshot = cms.CurrentSnapshot;
                var activeMembers = snapshot.Members.Values
                    .Where(m => m.Status == SiloStatus.Active)
                    .Select(m => m.SiloAddress)
                    .ToImmutableHashSet();
                siloViews.Add((silo.SiloAddress, snapshot.Version, activeMembers));
            }

            // Check if all silos agree and see exactly the expected set
            if (siloViews.Count > 0)
            {
                var allMatch = siloViews.All(v => v.ActiveMembers.SetEquals(expectedAddresses));

                if (allMatch)
                {
                    output.WriteLine($"WaitForClusterMembershipAsync: All {siloViews.Count} silos agree on {expectedAddresses.Count} active members");
                    return;
                }

                // Log progress periodically
                if (stopwatch.Elapsed - lastLogTime > logInterval)
                {
                    var report = siloViews.Select(v =>
                    {
                        var missing = expectedAddresses.Except(v.ActiveMembers);
                        var extra = v.ActiveMembers.Except(expectedAddresses);
                        return $"{v.Silo} (v{v.Version}): missing=[{string.Join(",", missing)}] extra=[{string.Join(",", extra)}]";
                    });
                    output.WriteLine($"WaitForClusterMembershipAsync: Views don't match yet. Expected: [{string.Join(",", expectedAddresses)}] Status: {string.Join("; ", report)}");
                    lastLogTime = stopwatch.Elapsed;
                }
            }

            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Check if we were cancelled by the test (not timeout)
        cancellationToken.ThrowIfCancellationRequested();

        // Timeout - log details
        foreach (var silo in GetAllActiveSilos())
        {
            var handle = (InProcessSiloHandle)silo;
            var cms = handle.SiloHost.Services.GetRequiredService<IClusterMembershipService>();
            var snapshot = cms.CurrentSnapshot;
            var memberStatuses = snapshot.Members.Values
                .Select(m => $"{m.SiloAddress}={m.Status}")
                .ToList();
            output.WriteLine($"TIMEOUT: Silo {silo.SiloAddress} version={snapshot.Version} sees members [{string.Join(", ", memberStatuses)}]");
        }

        throw new TimeoutException("Cluster membership did not converge prior to cancellation.");
    }
    #endregion
}

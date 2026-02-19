using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace UnitTests;

/// <summary>
/// Tests for the Orleans grain directory partition functionality.
/// 
/// The grain directory is a distributed data structure that maps grain identities
/// to their current activation locations (silo addresses). Key concepts:
/// 
/// - Single activation constraint: Each grain ID should have at most one activation
/// - Directory partitioning: The directory is partitioned across silos for scalability
/// - Silo death handling: Dead silo entries must be cleaned up to allow new activations
/// - Race condition handling: Concurrent activation requests must be properly synchronized
/// 
/// These tests verify critical scenarios:
/// - Overriding entries pointing to dead silos
/// - Preventing duplicate activations on live silos
/// - Conditional updates using previousAddress parameter
/// - Filtering out entries for dead silos during lookups
/// 
/// This is essential for maintaining Orleans' single activation guarantee.
/// </summary>
[TestCategory("BVT"), TestCategory("GrainDirectory")]
public class GrainDirectoryPartitionTests
{
    private readonly LocalGrainDirectoryPartition _target;
    private readonly MockSiloStatusOracle _siloStatusOracle;
    private static readonly SiloAddress LocalSiloAddress =  SiloAddress.FromParsableString("127.0.0.1:11111@123");
    private static readonly SiloAddress OtherSiloAddress =  SiloAddress.FromParsableString("127.0.0.2:11111@456");

    public GrainDirectoryPartitionTests()
    {
        _siloStatusOracle = new MockSiloStatusOracle();
        _target = new LocalGrainDirectoryPartition(
            _siloStatusOracle,
            Options.Create(new GrainDirectoryOptions()),
            new LoggerFactory());
    }

    /// <summary>
    /// Tests that directory entries pointing to dead silos can be overridden.
    /// When a silo dies, its grain activations become invalid. The directory
    /// must allow new activations to replace these dead entries to maintain
    /// availability. This is crucial for recovery after silo failures.
    /// </summary>
    [Fact]
    public void OverrideDeadEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.Create("testGrain", "myKey");
        var firstGrainAddress = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var firstRegister = _target.AddSingleActivation(firstGrainAddress, previousAddress: null);
        Assert.Equal(firstGrainAddress, firstRegister.Address);

        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        // Previous entry is now pointing to a dead silo, it should be possible to override it now
        var secondGrainAddress = GrainAddress.NewActivationAddress(LocalSiloAddress, grainId);
        var secondRegister = _target.AddSingleActivation(secondGrainAddress, previousAddress: null);
        Assert.Equal(secondGrainAddress, secondRegister.Address);
    }

    /// <summary>
    /// Verifies that the directory rejects attempts to register activations
    /// on silos that are already known to be dead. This prevents creating
    /// entries that would immediately be invalid, avoiding unnecessary
    /// cleanup work and potential race conditions.
    /// </summary>
    [Fact]
    public void DoNotInsertInvalidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        var grainId = GrainId.Create("testGrain", "myKey");
        var grainAddress = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert invalid entry, pointing to dead silo
       Assert.Throws<OrleansException>(() => _target.AddSingleActivation(grainAddress, previousAddress: null));
    }

    /// <summary>
    /// Tests the single activation guarantee by ensuring valid entries
    /// cannot be overridden by new activation attempts. This prevents
    /// duplicate activations when multiple silos try to activate the
    /// same grain concurrently.
    /// </summary>
    [Fact]
    public void DoNotOverrideValidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.Create("testGrain", "myKey");
        var grainAddress = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var register = _target.AddSingleActivation(grainAddress, previousAddress: null);
        Assert.Equal(grainAddress, register.Address);

        // Previous entry is still valid, it should not be possible to override it
        var newGrainAddress = GrainAddress.NewActivationAddress(LocalSiloAddress, grainId);
        var newRegister = _target.AddSingleActivation(newGrainAddress, previousAddress: null);
        Assert.Equal(grainAddress, newRegister.Address);
    }

    /// <summary>
    /// Tests conditional update functionality using the previousAddress parameter.
    /// This allows explicit handoff scenarios where an activation is intentionally
    /// moved from one silo to another, but only if the current state matches
    /// expectations (optimistic concurrency control).
    /// </summary>
    [Fact]
    public void OverrideValidEntryIfMatchesTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.Create("testGrain", "myKey");
        var grainAddress = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var register = _target.AddSingleActivation(grainAddress, previousAddress: null);
        Assert.Equal(grainAddress, register.Address);

        // Previous entry is still valid, but it should be possible to override it we provide it as the "previousAddress" to the AddSingleActivation call.
        var newGrainAddress = GrainAddress.NewActivationAddress(LocalSiloAddress, grainId);
        var newRegister = _target.AddSingleActivation(newGrainAddress, previousAddress: grainAddress);
        Assert.Equal(newGrainAddress, newRegister.Address);
    }

    /// <summary>
    /// Verifies that conditional updates fail when the previousAddress doesn't
    /// match the current entry. This prevents race conditions where the directory
    /// state changed between read and update operations, ensuring consistency
    /// in distributed scenarios.
    /// </summary>
    [Fact]
    public void DoNotOverrideValidEntryIfNoMatchTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.Create("testGrain", "myKey");
        var grainAddress = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);
        var nonMatchingAddress = new GrainAddress
        {
            GrainId = grainAddress.GrainId,
            ActivationId = ActivationId.NewId(),
            SiloAddress = OtherSiloAddress,
            MembershipVersion = new MembershipVersion(grainAddress.MembershipVersion.Value + 1),
        };

        // Insert valid entry, pointing to valid silo
        var register = _target.AddSingleActivation(grainAddress, previousAddress: null);
        Assert.Equal(grainAddress, register.Address);

        // Previous entry is still valid and we provided a non-matching previousAddress, so the existing value should not be overridden.
        var newGrainAddress = GrainAddress.NewActivationAddress(LocalSiloAddress, grainId);
        var newRegister = _target.AddSingleActivation(newGrainAddress, previousAddress: nonMatchingAddress);
        Assert.Equal(grainAddress, newRegister.Address);
    }

    /// <summary>
    /// Tests that lookups filter out entries pointing to dead silos.
    /// Even if the directory contains stale entries, lookups should
    /// return null rather than invalid addresses, allowing the caller
    /// to create a new activation on a healthy silo.
    /// </summary>
    [Fact]
    public void DoNotReturnInvalidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.Create("testGrain", "myKey");
        var grainAddress1 = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var register1 = _target.AddSingleActivation(grainAddress1, previousAddress: null);
        Assert.Equal(grainAddress1, register1.Address);

        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        // Previous entry is no longer still valid, it should not be returned
        var lookup = _target.LookUpActivation(grainId);
        Assert.Null(lookup.Address);
    }

    /// <summary>
    /// Mock implementation of ISiloStatusOracle for testing.
    /// The silo status oracle provides membership information about
    /// which silos are alive, dead, or in other states. The grain
    /// directory uses this to validate entries and make placement decisions.
    /// </summary>
    private class MockSiloStatusOracle : ISiloStatusOracle
    {
        private readonly Dictionary<SiloAddress, SiloStatus> _content = new();

        public MockSiloStatusOracle(SiloAddress siloAddress = null)
        {
            SiloAddress = siloAddress ?? LocalSiloAddress;
            _content[SiloAddress] = SiloStatus.Active;
        }

        public SiloStatus CurrentStatus => SiloStatus.Active;

        public string SiloName => "TestSilo";

        public SiloAddress SiloAddress { get; }

        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            if (_content.TryGetValue(siloAddress, out var status))
            {
                return status;
            }
            return SiloStatus.None;
        }

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            return onlyActive
                ? new Dictionary<SiloAddress, SiloStatus>(_content.Where(kvp => kvp.Value == SiloStatus.Active))
                : new Dictionary<SiloAddress, SiloStatus>(_content);
        }

        public ImmutableArray<SiloAddress> GetActiveSilos() => _content.Keys.ToImmutableArray();

        public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status) => _content[siloAddress] = status;

        public bool IsDeadSilo(SiloAddress silo) => GetApproximateSiloStatus(silo) == SiloStatus.Dead;

        public bool IsFunctionalDirectory(SiloAddress siloAddress) => !GetApproximateSiloStatus(siloAddress).IsTerminating();

        #region Not Implemented
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => throw new NotImplementedException();

        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName) => throw new NotImplementedException();

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => throw new NotImplementedException();
        #endregion
    }
}

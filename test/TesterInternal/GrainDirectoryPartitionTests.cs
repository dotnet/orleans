using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace UnitTests;

[TestCategory("BVT"), TestCategory("GrainDirectory")]
public class GrainDirectoryPartitionTests
{
    private readonly GrainDirectoryPartition _target;
    private readonly MockSiloStatusOracle _siloStatusOracle;
    private static readonly SiloAddress LocalSiloAddress =  SiloAddress.FromParsableString("127.0.0.1:11111@123");
    private static readonly SiloAddress OtherSiloAddress =  SiloAddress.FromParsableString("127.0.0.2:11111@456");

    public GrainDirectoryPartitionTests()
    {
        _siloStatusOracle = new MockSiloStatusOracle();
        _target = new GrainDirectoryPartition(
            _siloStatusOracle,
            Options.Create(new GrainDirectoryOptions()),
            new LoggerFactory());
    }

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

    [Fact]
    public void DoNotInsertInvalidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        var grainId = GrainId.Create("testGrain", "myKey");
        var grainAddress = GrainAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert invalid entry, pointing to dead silo
       Assert.Throws<OrleansException>(() => _target.AddSingleActivation(grainAddress, previousAddress: null));
    }

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

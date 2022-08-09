using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
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
            null,
            new LoggerFactory());
    }

    [Fact]
    public void OverrideDeadEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.NewId();
        var firstGrainAddress = ActivationAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var firstRegister = _target.AddSingleActivation(firstGrainAddress.Grain, firstGrainAddress.Activation, firstGrainAddress.Silo, GrainDirectoryEntryStatus.ClusterLocal);
        Assert.Equal(firstGrainAddress, firstRegister.Address);

        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        // Previous entry is now pointing to a dead silo, it should be possible to override it now
        var secondGrainAddress = ActivationAddress.NewActivationAddress(LocalSiloAddress, grainId);
        var secondRegister = _target.AddSingleActivation(secondGrainAddress.Grain, secondGrainAddress.Activation, secondGrainAddress.Silo, GrainDirectoryEntryStatus.ClusterLocal);
        Assert.Equal(secondGrainAddress, secondRegister.Address);
    }

    [Fact]
    public void DotNotInsertInvalidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        var grainId = GrainId.NewId();
        var grainAddress = ActivationAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert invalid entry, pointing to dead silo
        Assert.Throws<OrleansException>(() => _target.AddSingleActivation(grainAddress.Grain, grainAddress.Activation, grainAddress.Silo, GrainDirectoryEntryStatus.ClusterLocal));
    }

    [Fact]
    public void DotNotOverrideValidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.NewId();
        var grainAddress = ActivationAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var register = _target.AddSingleActivation(grainAddress.Grain, grainAddress.Activation, grainAddress.Silo, GrainDirectoryEntryStatus.ClusterLocal);
        Assert.Equal(grainAddress, register.Address);

        // Previous entry is still valid, it should not be possible to override it
        var newGrainAddress = ActivationAddress.NewActivationAddress(LocalSiloAddress, grainId);
        var newRegister = _target.AddSingleActivation(newGrainAddress.Grain, newGrainAddress.Activation, newGrainAddress.Silo, GrainDirectoryEntryStatus.ClusterLocal);
        Assert.Equal(grainAddress, newRegister.Address);
    }

    [Fact]
    public void DotNotReturnInvalidEntryTest()
    {
        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Active);

        var grainId = GrainId.NewId();
        var grainAddress = ActivationAddress.NewActivationAddress(OtherSiloAddress, grainId);

        // Insert valid entry, pointing to valid silo
        var register = _target.AddSingleActivation(grainAddress.Grain, grainAddress.Activation, grainAddress.Silo, GrainDirectoryEntryStatus.ClusterLocal);
        Assert.Equal(grainAddress, register.Address);

        _siloStatusOracle.SetSiloStatus(OtherSiloAddress, SiloStatus.Dead);

        // Previous entry is no longer still valid, it should not be returned
        var lookup = _target.LookUpActivations(grainId);
        // Addresses can be null if there is no entry for this grain
        if (lookup.Addresses != null)
        {
            Assert.Empty(lookup.Addresses);
        }
    }

    private class MockSiloStatusOracle : ISiloStatusOracle
    {
        private Dictionary<SiloAddress, SiloStatus> _content = new();

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
#if NETCOREAPP
            return onlyActive
                ? new Dictionary<SiloAddress, SiloStatus>(_content.Where(kvp => kvp.Value == SiloStatus.Active))
                : new Dictionary<SiloAddress, SiloStatus>(_content);
#else
            if (onlyActive)
            {
                var result = new Dictionary<SiloAddress, SiloStatus>();
                foreach (var entry in _content.Where(kvp => kvp.Value == SiloStatus.Active))
                {
                    result.Add(entry.Key, entry.Value);
                }
                return result;
            }
            else
            {
                return new Dictionary<SiloAddress, SiloStatus>(_content);
            }
#endif
        }

        public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status) => _content[siloAddress] = status;

        public bool IsDeadSilo(SiloAddress silo) => GetApproximateSiloStatus(silo) == SiloStatus.Dead;

        public bool IsFunctionalDirectory(SiloAddress siloAddress) => !GetApproximateSiloStatus(siloAddress).IsTerminating();

#region Not Implemented
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => throw new NotImplementedException();
        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName) => throw new NotImplementedException();
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => throw new NotImplementedException();
        public Task Start() => throw new NotImplementedException();
        public Task BecomeActive() => throw new NotImplementedException();
        public Task ShutDown() => throw new NotImplementedException();
        public Task Stop() => throw new NotImplementedException();
        public Task KillMyself() => throw new NotImplementedException();
#endregion
    }
}

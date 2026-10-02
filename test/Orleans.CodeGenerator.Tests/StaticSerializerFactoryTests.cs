using Orleans.Serialization.ContextSmoke;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class StaticSerializerFactoryTests
{
    [Fact]
    public void CyclicConstructionPublishesCompletedGraphs() => StaticFactoryContracts.CyclicConstructionPublishesCompletedGraphs();

    [Fact]
    public void FailedCyclicConstructionRollsBack() => StaticFactoryContracts.FailedCyclicConstructionRollsBack();

    [Fact]
    public void CaughtNestedFailureFaultsTheWholeGraph() => StaticFactoryContracts.CaughtNestedFailureFaultsTheWholeGraph();

    [Fact]
    public void SupplementalFactoriesPreserveAutomaticMetadata() => StaticFactoryContracts.SupplementalFactoriesPreserveAutomaticMetadata();

    [Fact]
    public void AutomaticCacheEntriesRollBackWithFactoryFailures() => StaticFactoryContracts.AutomaticCacheEntriesRollBackWithFactoryFailures();

    [Fact]
    public void MixedConstructionCyclesFailBeforePublication() => StaticFactoryContracts.MixedConstructionCyclesFailBeforePublication();

    [Fact]
    public void DiSingletonsCannotRetainPendingServices() => StaticFactoryContracts.DiSingletonsCannotRetainPendingServices();

    [Fact]
    public void SingletonFirstLookupCannotInvertGraphLock() => StaticFactoryContracts.SingletonFirstLookupCannotInvertGraphLock();

    [Fact]
    public void CaughtAutomaticAndMissingFailuresFaultGraph() => StaticFactoryContracts.CaughtAutomaticAndMissingFailuresFaultGraph();

    [Fact]
    public void HolderCachesOnlyPublishedDependencies() => StaticFactoryContracts.HolderCachesOnlyPublishedDependencies();

    [Fact]
    public void DirectProviderServicesRemainGraphOwned() => StaticFactoryContracts.DirectProviderServicesRemainGraphOwned();

    [Fact]
    public void KeyedDescriptorsDoNotShadowUnkeyedInstances() => StaticFactoryContracts.KeyedDescriptorsDoNotShadowUnkeyedInstances();

    [Fact]
    public void KeyedOnlyDescriptorsDoNotSelectDependencyConstructors() => StaticFactoryContracts.KeyedOnlyDescriptorsDoNotSelectDependencyConstructors();

    [Fact]
    public void CaughtBaseCodecSpecializationFailureFaultsGraph() => StaticFactoryContracts.CaughtBaseCodecSpecializationFailureFaultsGraph();

    [Fact]
    public void DefaultActivatorFactoriesPreserveConstructionSemantics() => StaticFactoryContracts.DefaultActivatorFactoriesPreserveConstructionSemantics();

    [Fact]
    public void KeyedFacadePreservesProviderCapabilitiesOutsideConstruction() => StaticFactoryContracts.KeyedFacadePreservesProviderCapabilitiesOutsideConstruction();

    [Fact]
    public void CapturedKeyedFacadeGuardsPendingConstruction() => StaticFactoryContracts.CapturedKeyedFacadeGuardsPendingConstruction();
}

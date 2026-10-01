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
}

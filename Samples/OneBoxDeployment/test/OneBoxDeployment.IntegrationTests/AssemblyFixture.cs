using OneBoxDeployment.IntegrationTests;
using Xunit;
using Xunit.Extensions.Ordering;

//Every assembly level fixture needs to be defined like this.
[assembly: AssemblyFixture(typeof(IntegrationTestFixture))]

[assembly: CollectionBehavior(DisableTestParallelization = false)]
[assembly: TestFramework("Xunit.Extensions.Ordering.TestFramework", "Xunit.Extensions.Ordering")]
[assembly: TestCaseOrderer("Xunit.Extensions.Ordering.TestCaseOrderer", "Xunit.Extensions.Ordering")]
[assembly: TestCollectionOrderer("Xunit.Extensions.Ordering.CollectionOrderer", "Xunit.Extensions.Ordering")]
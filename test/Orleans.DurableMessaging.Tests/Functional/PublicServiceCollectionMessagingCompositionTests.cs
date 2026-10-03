using Orleans.DurableMessaging.Tests.Support;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class PublicServiceCollectionMessagingCompositionTests()
    : DurableMessagingGrainTypeConfiguratorTests(new BootstrapClusterFixture(useServiceCollection: true));

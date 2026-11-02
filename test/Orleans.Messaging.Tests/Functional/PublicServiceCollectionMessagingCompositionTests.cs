using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class PublicServiceCollectionMessagingCompositionTests()
    : MessagingGrainTypeConfiguratorTests(new BootstrapClusterFixture(useServiceCollection: true));

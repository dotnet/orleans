using Orleans.Durable.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Durable.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class PublicServiceCollectionMessagingCompositionTests()
    : MessagingCompositionTests(new BootstrapClusterFixture(useServiceCollection: true));

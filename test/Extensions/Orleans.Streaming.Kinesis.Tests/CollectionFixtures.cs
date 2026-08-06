using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
public sealed class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture>;

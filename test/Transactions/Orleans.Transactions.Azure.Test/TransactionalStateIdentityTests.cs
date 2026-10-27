using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Orleans.Hosting;
using Orleans.Transactions.AzureStorage.Tests;
using Xunit;

namespace Orleans.Transactions.Azure.Tests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Transactions"), TestCategory("BVT")]
public sealed class TransactionalStateIdentityTests
{
    [Fact]
    public async Task StorageProfileRegistersStateIdentityForStrictJsonBinding()
    {
        var registrations = new ServiceCollection();
        registrations.AddOrleansClient(builder =>
        {
            builder.UseLocalhostClustering();
            new TestFixture.ClientBuilderConfigurator().Configure(builder.Configuration, builder);
        });
        await using var services = registrations.BuildServiceProvider();
        var settings = TransactionalStateFactory.GetJsonSerializerSettings(services);
        var original = new TestState { State = 37, Payload = "stored state" };

        var json = JsonConvert.SerializeObject(original, settings);
        Assert.Contains("$type", json);
        Assert.Equal(original, JsonConvert.DeserializeObject<TestState>(json, settings));
    }
}

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Xunit;

namespace Tester.AzureUtils;

/// <summary>
/// Tests for Azure Queue Stream Provider configuration builder, validating various configuration scenarios.
/// </summary>
public class AzureQueueStreamProviderBuilderTests
{
	/// <summary>
	/// Verifies that missing connection string results in null QueueServiceClient.
	/// </summary>
	[Fact]
	public void Missing_ConnectionString()
	{
		string json = """
		{
			"Orleans": {
				"Streaming": {
					"AzureQueueProvider": {
						"ProviderType": "AzureQueueStorage",
						"QueueNames": [
							"q1"
						]
					}
				}
			}
		}
		""";

		var queueOptions = ConfigureSilo(json).Services.BuildServiceProvider().GetOptionsByName<AzureQueueOptions>(null);

		Assert.Null(queueOptions.QueueServiceClient);
	}

	/// <summary>
	/// Verifies that minimal required configuration creates valid QueueServiceClient with default settings.
	/// </summary>
	[Fact]
	public void Minimal_Configuration()
	{
		string json = """
		{
			"Orleans": {
				"Streaming": {
					"AzureQueueProvider": {
						"ProviderType": "AzureQueueStorage",
						"ConnectionString": "UseDevelopmentStorage=true",
						"QueueNames": [
							"q1"
						]
					}
				}
			}
		}
		""";

		var queueOptions = ConfigureSilo(json).Services.BuildServiceProvider().GetOptionsByName<AzureQueueOptions>(null);

		Assert.NotNull(queueOptions.QueueServiceClient);
		Assert.Equal("devstoreaccount1", queueOptions.QueueServiceClient.AccountName);
		Assert.Equal(["q1"], queueOptions.QueueNames);
		Assert.Null(queueOptions.MessageVisibilityTimeout);
	}

	/// <summary>
	/// Verifies that all configuration options are properly parsed and applied.
	/// </summary>
	[Fact]
	public void Full_Configuration()
	{
		string json = """
		{
			"Orleans": {
				"Streaming": {
					"AzureQueueProvider": {
						"ProviderType": "AzureQueueStorage",
						"ConnectionString": "UseDevelopmentStorage=true",
						"MessageVisibilityTimeout": "00:00:37",
						"QueueNames": [
							"q1",
							"q2"
						]
					}
				}
			}
		}
		""";

		var queueOptions = ConfigureSilo(json).Services.BuildServiceProvider().GetOptionsByName<AzureQueueOptions>(null);

		Assert.NotNull(queueOptions.QueueServiceClient);
		Assert.Equal("devstoreaccount1", queueOptions.QueueServiceClient.AccountName);
		Assert.Equal(["q1", "q2"], queueOptions.QueueNames);
		Assert.Equal(TimeSpan.FromSeconds(37), queueOptions.MessageVisibilityTimeout);
	}

	static TestSiloBuilder ConfigureSilo(string json)
	{
		var siloBuilder = new TestSiloBuilder(json);
		var aqsBuilder = new AzureQueueStreamProviderBuilder();
		aqsBuilder.Configure(siloBuilder, null, siloBuilder.Configuration.GetSection("Orleans:Streaming:AzureQueueProvider"));
		return siloBuilder;
	}

	class TestSiloBuilder(string json) : ISiloBuilder
	{
		public IServiceCollection Services { get; } = new ServiceCollection();

		public IConfiguration Configuration { get; } = GetConfig(json);
	}

	static IConfigurationRoot GetConfig(string json) => new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
}

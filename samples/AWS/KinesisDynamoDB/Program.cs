using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;

var settings = AwsSampleSettings.FromEnvironment();
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = settings.ClusterId;
            options.ServiceId = settings.ServiceId;
        })
        .UseDynamoDBClustering(options =>
        {
            options.Service = settings.Region;
            options.TableName = $"{settings.ResourcePrefix}Silos";
            options.UseProvisionedThroughput = false;
        })
        .AddDynamoDBGrainStorageAsDefault(options =>
        {
            options.Service = settings.Region;
            options.ServiceId = settings.ServiceId;
            options.TableName = $"{settings.ResourcePrefix}GrainState";
            options.UseProvisionedThroughput = false;
        })
        .AddDynamoDBGrainStorage("PubSubStore", options =>
        {
            options.Service = settings.Region;
            options.ServiceId = settings.ServiceId;
            options.TableName = $"{settings.ResourcePrefix}PubSub";
            options.UseProvisionedThroughput = false;
        })
        .UseDynamoDBReminderService(options =>
        {
            options.Service = settings.Region;
            options.TableName = $"{settings.ResourcePrefix}Reminders";
            options.UseProvisionedThroughput = false;
        })
        .AddKinesisStreams(SampleConstants.StreamProvider, stream =>
        {
            stream.ConfigureKinesis(options =>
            {
                options.Region = settings.Region;
                options.StreamName = settings.StreamName;
            });
            stream.UseDynamoDBCheckpointer(options =>
            {
                options.Service = settings.Region;
                options.TableName = $"{settings.ResourcePrefix}StreamCheckpoints";
                options.PersistInterval = TimeSpan.FromSeconds(10);
            });
        });
});

builder.Services.AddHostedService<SamplePublisher>();

await builder.Build().RunAsync();

internal sealed record AwsSampleSettings(
    string Region,
    string StreamName,
    string ResourcePrefix,
    string ClusterId,
    string ServiceId)
{
    public static AwsSampleSettings FromEnvironment()
    {
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
            ?? "us-east-1";

        return new(
            Region: region,
            StreamName: Environment.GetEnvironmentVariable("ORLEANS_KINESIS_STREAM") ?? "orleans-sample",
            ResourcePrefix: Environment.GetEnvironmentVariable("ORLEANS_DYNAMODB_PREFIX") ?? "OrleansSample",
            ClusterId: Environment.GetEnvironmentVariable("ORLEANS_CLUSTER_ID") ?? "aws-kinesis-sample",
            ServiceId: Environment.GetEnvironmentVariable("ORLEANS_SERVICE_ID") ?? "aws-kinesis-sample");
    }
}

internal sealed class SamplePublisher(
    IGrainFactory grainFactory,
    ILogger<SamplePublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grain = grainFactory.GetGrain<IStreamProcessorGrain>(SampleConstants.StreamId);
        await grain.InitializeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = $"Event created at {DateTimeOffset.UtcNow:O}";
            await grain.PublishAsync(message, stoppingToken);
            var state = await grain.GetStateAsync(stoppingToken);
            logger.LogInformation(
                "Published an event. The processor has persisted {EventCount} events and {ReminderCount} reminder ticks",
                state.EventCount,
                state.ReminderCount);

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

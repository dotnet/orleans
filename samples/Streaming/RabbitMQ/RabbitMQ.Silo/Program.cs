using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Streaming.RabbitMQ.Configurators;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using RabbitMQ.Stream.Client;

var settings = RabbitMQSampleSettings.FromEnvironment();
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddMemoryGrainStorage("PubSubStore")
        .AddRabbitMQStreams(SampleConstants.StreamProvider, stream =>
        {
            stream.ConfigureRabbitMQ(optionsBuilder => optionsBuilder.Configure(options =>
            {
                var endpoint = new DnsEndPoint(settings.Host, settings.Port);
                options.StreamSystemConfig = new StreamSystemConfig
                {
                    UserName = settings.UserName,
                    Password = settings.Password,
                    Endpoints = [endpoint],
                    AddressResolver = new AddressResolver(endpoint),
                };
                options.IntervalToUpdateOffset = TimeSpan.FromSeconds(5);
                options.ConnectionRetry = new RabbitMQConnectionRetryOptions
                {
                    MaxAttempts = 10,
                    Delay = TimeSpan.FromSeconds(2),
                };
            }));
            stream.ConfigurePartitioning(settings.PartitionCount);
        });
});

builder.Services.AddHostedService<SamplePublisher>();

await builder.Build().RunAsync();

internal sealed record RabbitMQSampleSettings(
    string Host,
    int Port,
    string UserName,
    string Password,
    int PartitionCount)
{
    public static RabbitMQSampleSettings FromEnvironment() =>
        new(
            Host: Environment.GetEnvironmentVariable("RABBITMQ_STREAM_ADDRESS") ?? "127.0.0.1",
            Port: ParseInt32("RABBITMQ_STREAM_PORT", 5552),
            UserName: Environment.GetEnvironmentVariable("RABBITMQ_STREAM_USER") ?? "guest",
            Password: Environment.GetEnvironmentVariable("RABBITMQ_STREAM_PASSWORD") ?? "guest",
            PartitionCount: ParseInt32("ORLEANS_RABBITMQ_PARTITIONS", 4));

    private static int ParseInt32(string variable, int defaultValue) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : defaultValue;
}

internal sealed class SamplePublisher(
    IGrainFactory grainFactory,
    ILogger<SamplePublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var producer = grainFactory.GetGrain<IStreamProducerGrain>("sample-publisher");
        var sequence = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = new SampleEvent(++sequence, DateTimeOffset.UtcNow);
            await producer.PublishAsync(SampleConstants.StreamId, message);
            logger.LogInformation("Published event {Sequence}", message.Sequence);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

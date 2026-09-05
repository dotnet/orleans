// <broadcast_channel_silo_program>
using System.Text.Json;
using BroadcastChannel.GrainInterfaces;
using BroadcastChannel.Silo.Options;
using BroadcastChannel.Silo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

await Host.CreateDefaultBuilder(args)
    .UseOrleans((context, silo) =>
    {
        silo.Services.AddOptions<AlphaVantageOptions>()
            .Bind(context.Configuration.GetSection(nameof(AlphaVantageOptions)));
        silo.Services.AddTransient<StockClient>();
        silo.Services.AddHttpClient<StockClient>(client =>
        {
            client.BaseAddress = new("https://www.alphavantage.co/");
        });
        silo.Services.AddSerializer(
            serializer => serializer.AddJsonSerializer(
                isSupported: type => type?.Namespace?.StartsWith(
                        nameof(BroadcastChannel),
                        StringComparison.Ordinal) ?? false,
                jsonSerializerOptions: new(JsonSerializerDefaults.Web)));
        silo.Services.AddHostedService<StockWorker>();
        silo.UseLocalhostClustering();
        silo.AddBroadcastChannel(
            ChannelNames.LiveStockTicker,
            builder => builder.Configure(
                options => options.FireAndForgetDelivery = false));
    })
    .RunConsoleAsync();
// </broadcast_channel_silo_program>

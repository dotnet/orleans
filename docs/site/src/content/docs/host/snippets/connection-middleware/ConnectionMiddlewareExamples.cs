using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Messaging;

namespace ConnectionMiddlewareSnippets;

internal static class ConnectionMiddlewareExamples
{
    // <RegisterMiddleware>
    public static void RegisterFromDependencyInjection(
        IServiceCollection services,
        IConnectionBuilder connectionBuilder)
    {
        services.AddSingleton<MyClientSideMiddleware>();
        connectionBuilder.UseMiddleware<MyClientSideMiddleware>();
    }

    public static void RegisterInstance(IConnectionBuilder connectionBuilder)
    {
        // The caller owns this shared instance and is responsible for disposing it.
        connectionBuilder.UseMiddleware(new MyClientSideMiddleware());
    }
    // </RegisterMiddleware>

    // <SiloPipelines>
    public static void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<MyClientSideMiddleware>();
        siloBuilder.Services.AddSingleton<MyServerSideMiddleware>();

        siloBuilder.Configure<SiloConnectionOptions>(options =>
        {
            // Connections this silo makes to other silos.
            options.ConfigureSiloOutboundConnection(connectionBuilder =>
            {
                connectionBuilder.UseMiddleware<MyClientSideMiddleware>();
            });

            // Connections this silo accepts from other silos.
            options.ConfigureSiloInboundConnection(connectionBuilder =>
            {
                connectionBuilder.UseMiddleware<MyServerSideMiddleware>();
            });

            // Connections this silo accepts from Orleans clients through the gateway.
            options.ConfigureGatewayInboundConnection(connectionBuilder =>
            {
                connectionBuilder.UseMiddleware<MyServerSideMiddleware>();
            });
        });
    }
    // </SiloPipelines>

    // <ClientPipeline>
    public static void ConfigureClient(IClientBuilder clientBuilder)
    {
        clientBuilder.Services.AddSingleton<MyClientSideMiddleware>();

        clientBuilder.Configure<ClientConnectionOptions>(options =>
        {
            options.ConfigureConnection(connectionBuilder =>
            {
                connectionBuilder.UseMiddleware<MyClientSideMiddleware>();
            });
        });
    }
    // </ClientPipeline>
}

internal sealed class MyClientSideMiddleware : IConnectionMiddleware
{
    public async Task OnConnectionAsync(
        ConnectionContext context,
        ConnectionDelegate next)
    {
        var requestPayload = Encoding.UTF8.GetBytes("hello");
        await ConnectionFrameHelper.WriteFrameAsync(
            context, frameType: 0x01, requestPayload, context.ConnectionClosed);

        var (frameType, payload) = await ConnectionFrameHelper.ReadFrameAsync(
            context, context.ConnectionClosed);

        if (frameType != 0x02
            || !string.Equals(
                Encoding.UTF8.GetString(payload),
                "ok",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected handshake response.");
        }

        await next(context);
    }
}

// <ServerMiddleware>
internal sealed class MyServerSideMiddleware : IConnectionMiddleware
{
    public async Task OnConnectionAsync(
        ConnectionContext context,
        ConnectionDelegate next)
    {
        // Read one frame of the custom handshake protocol.
        var (frameType, payload) = await ConnectionFrameHelper.ReadFrameAsync(
            context, context.ConnectionClosed);

        if (frameType != 0x01
            || !string.Equals(
                Encoding.UTF8.GetString(payload),
                "hello",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected handshake request.");
        }

        var responsePayload = Encoding.UTF8.GetBytes("ok");
        await ConnectionFrameHelper.WriteFrameAsync(
            context, frameType: 0x02, responsePayload, context.ConnectionClosed);

        // Continue the pipeline; Orleans's own handshake and framing run after this.
        await next(context);
    }
}
// </ServerMiddleware>

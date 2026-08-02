using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;

namespace OrleansMigrationSnippets;

public static class Orleans10MigrationConfiguration
{
    // <timeout_cancellation>
    public static void ConfigureTimeoutCancellation(ISiloBuilder siloBuilder)
    {
        siloBuilder.Configure<SiloMessagingOptions>(options =>
        {
            options.CancelRequestOnTimeout = true;
        });
    }

    public static void ConfigureTimeoutCancellation(IClientBuilder clientBuilder)
    {
        clientBuilder.Configure<ClientMessagingOptions>(options =>
        {
            options.CancelRequestOnTimeout = true;
        });
    }
    // </timeout_cancellation>

    // <random_placement>
    public static void KeepRandomPlacement(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<PlacementStrategy, RandomPlacement>();
    }
    // </random_placement>

    // <call_filters>
    public static void ConfigureCallFilters(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddIncomingGrainCallFilter(async context =>
        {
            await context.Invoke();
        });

        siloBuilder.AddOutgoingGrainCallFilter<MyOutgoingCallFilter>();
    }
    // </call_filters>
}

public sealed class MyOutgoingCallFilter : IOutgoingGrainCallFilter
{
    public Task Invoke(IOutgoingGrainCallContext context) => context.Invoke();
}

public interface ICancelableWorkGrain : IGrainWithStringKey
{
    // <grain_cancellation>
    Task RunAsync(CancellationToken cancellationToken);
    // </grain_cancellation>
}

public sealed class TimerGrain : Grain
{
    private IGrainTimer? _timer;

    // <grain_timer>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _timer = this.RegisterGrainTimer(
            callback: DoWorkAsync,
            options: new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromSeconds(1),
                Period = TimeSpan.FromSeconds(10),
                Interleave = true
            });

        return Task.CompletedTask;
    }

    private static Task DoWorkAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
    // </grain_timer>
}

// <serialized_state>
[GenerateSerializer]
public sealed class CounterState
{
    [Id(0)]
    public int Value { get; set; }
}
// </serialized_state>

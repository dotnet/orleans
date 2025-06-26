using DashboardToy.Frontend.Data;
using Microsoft.AspNetCore.Mvc;
using Orleans.Configuration;
using Orleans.Placement.Repartitioning;

var builder = WebApplication.CreateBuilder(args);
builder.AddKeyedRedisClient("orleans-redis");
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
builder.UseOrleans(orleans =>
{
    orleans.AddActivationRepartitioner<HardLimitRule>();
    orleans.Configure<ActivationRepartitionerOptions>(o =>
    {
        o.MinRoundPeriod = TimeSpan.FromSeconds(5);
        o.MaxRoundPeriod = TimeSpan.FromSeconds(15);
        o.RecoveryPeriod = TimeSpan.FromSeconds(2);
    });
});
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Add services to the container.
builder.Services.AddSingleton<ClusterDiagnosticsService>();

var app = builder.Build();

var clusterDiagnosticsService = app.Services.GetRequiredService<ClusterDiagnosticsService>();
app.MapGet("/data.json", ([FromServices] ClusterDiagnosticsService clusterDiagnosticsService) => clusterDiagnosticsService.GetGrainCallFrequencies());
app.MapPost("/reset", async ([FromServices] IGrainFactory grainFactory) =>
{
    await grainFactory.GetGrain<ILoaderGrain>("root").Reset();
});
app.MapPost("/add", async ([FromServices] IGrainFactory grainFactory) =>
{
    await grainFactory.GetGrain<ILoaderGrain>("root").AddForest();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

await app.StartAsync();

var loadGrain = app.Services.GetRequiredService<IGrainFactory>().GetGrain<ILoaderGrain>("root");
await loadGrain.AddForest();
await loadGrain.AddForest();
await loadGrain.AddForest();

await app.WaitForShutdownAsync();

public interface ILoaderGrain : IGrainWithStringKey
{
    ValueTask AddForest();
    ValueTask Reset();
    ValueTask<int> GetResetCount();
}

public class LoaderGrain : Grain, ILoaderGrain
{
    private int _numForests = 0;
    private int _resetCount;

    public async ValueTask AddForest()
    {
        var forest = _numForests++;
        var loadGrain = GrainFactory.GetGrain<IFanOutGrain>(0, forest.ToString());
        await loadGrain.Ping();
    }

    public async ValueTask Reset()
    {
        ++_resetCount;
        _numForests = 0;
        await ServiceProvider.GetRequiredService<ClusterDiagnosticsService>().ResetAsync();
        await GrainFactory.GetGrain<IManagementGrain>(0).ResetGrainCallFrequencies();
    }

    public ValueTask<int> GetResetCount() => new(_resetCount);
}

public interface IFanOutGrain : IGrainWithIntegerCompoundKey
{
    public ValueTask Ping();
}

public class FanOutGrain : Grain, IFanOutGrain
{
    public const int FanOutFactor = 4;
    public const int MaxLevel = 2;
    private readonly List<IFanOutGrain> _children;

    public FanOutGrain()
    {
        var id = this.GetPrimaryKeyLong(out var forest);
        if (forest is null)
        {
            throw new InvalidOperationException("FanOutGrain must be created with a forest identifier (i.e, a grain key extension).");
        }

        var level = id == 0 ? 0 : (int)Math.Log(id, FanOutFactor);
        var numChildren = level < MaxLevel ? FanOutFactor : 0;
        _children = new List<IFanOutGrain>(numChildren);
        var childBase = (id + 1) * FanOutFactor;
        for (var i = 1; i <= numChildren; i++)
        {
            var child = GrainFactory.GetGrain<IFanOutGrain>(childBase + i, forest);
            _children.Add(child);
        }

        this.RegisterGrainTimer(() => Ping().AsTask(), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5));
    }

    public async ValueTask Ping()
    {
        var tasks = new List<ValueTask>(_children.Count);
        foreach (var child in _children)
        {
            tasks.Add(child.Ping());
        }

        // Wait for the tasks to complete.
        foreach (var task in tasks)
        {
            await task;
        }
    }
}

internal sealed class HardLimitRule : IImbalanceToleranceRule
{
    public bool IsSatisfiedBy(uint imbalance) => imbalance <= 30;
}

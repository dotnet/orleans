using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Dashboard.Metrics;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard.Implementation;

internal sealed class SiloGrainService : GrainService, ISiloGrainService
{
    private const int DefaultTimerIntervalMs = 1000; // 1 second
    private readonly Queue<SiloRuntimeStatistics> _statistics;
    private readonly Dictionary<string, StatCounter> _counters = [];
    private readonly DashboardOptions _options;
    private readonly IGrainProfiler _profiler;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SiloGrainService> _logger;
    private IDisposable _timer;
    private string _versionOrleans;
    private string _versionHost;

    public SiloGrainService(
        GrainId grainId,
        Silo silo,
        ILoggerFactory loggerFactory,
        IGrainProfiler profiler,
        IOptions<DashboardOptions> options,
        IGrainFactory grainFactory) : base(grainId, silo, loggerFactory)
    {
        _profiler = profiler;
        _options = options.Value;
        _grainFactory = grainFactory;
        _statistics = new Queue<SiloRuntimeStatistics>(_options.HistoryLength + 1);
        _logger = loggerFactory.CreateLogger<SiloGrainService>();
    }

    public override async Task Start()
    {
        foreach (var _ in Enumerable.Range(1, _options.HistoryLength))
        {
            _statistics.Enqueue(null);
        }

        var updateInterval = TimeSpan.FromMilliseconds(
            Math.Max(_options.CounterUpdateIntervalMs, DefaultTimerIntervalMs)
        );
        try
        {
            _timer = RegisterTimer(x => CollectStatistics((bool) x), true, updateInterval, updateInterval);

            await CollectStatistics(false);
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning("Not running in Orleans runtime");
        }

        await base.Start();
    }

    private async Task CollectStatistics(bool canDeactivate)
    {
        var managementGrain = _grainFactory.GetGrain<IManagementGrain>(0);
        try
        {
            var siloAddress = SiloAddress.FromParsableString(this.GetPrimaryKeyString());

            var results = (await managementGrain.GetRuntimeStatistics([siloAddress])).FirstOrDefault();

            _statistics.Enqueue(results);

            while (_statistics.Count > _options.HistoryLength)
            {
                _statistics.Dequeue();
            }
        }
        catch (Exception)
        {
            // we can't get the silo stats, it's probably dead, so kill the grain
            if (canDeactivate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    public Task SetVersion(string orleans, string host)
    {
        _versionOrleans = orleans;
        _versionHost = host;

        return Task.CompletedTask;
    }

    public Task<Immutable<Dictionary<string, string>>> GetExtendedProperties()
    {
        var results = new Dictionary<string, string>
        {
            ["HostVersion"] = _versionHost,
            ["OrleansVersion"] = _versionOrleans
        };

        return Task.FromResult(results.AsImmutable());
    }

    public Task ReportCounters(Immutable<StatCounter[]> reportCounters)
    {
        foreach (var counter in reportCounters.Value)
        {
            if (!string.IsNullOrWhiteSpace(counter.Name))
            {
                _counters[counter.Name] = counter;
            }
        }

        return Task.CompletedTask;
    }

    public Task<Immutable<SiloRuntimeStatistics[]>> GetRuntimeStatistics()
    {
        return Task.FromResult(_statistics.ToArray().AsImmutable());
    }

    public Task<Immutable<StatCounter[]>> GetCounters()
    {
        return Task.FromResult(_counters.Values.OrderBy(x => x.Name).ToArray().AsImmutable());
    }

    public Task Enable(bool enabled)
    {
        _profiler.Enable(enabled);

        return Task.CompletedTask;
    }
}

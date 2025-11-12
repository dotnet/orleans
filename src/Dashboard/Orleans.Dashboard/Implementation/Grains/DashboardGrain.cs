using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Dashboard.Implementation.Helpers;
using Orleans.Dashboard.Metrics.Details;
using Orleans.Dashboard.Metrics.History;
using Orleans.Dashboard.Metrics.TypeFormatting;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard.Implementation.Grains;

[Reentrant]
internal sealed class DashboardGrain : Grain, IDashboardGrain
{
    private readonly TraceHistory _history;
    private readonly ISiloDetailsProvider _siloDetailsProvider;
    private readonly ISiloGrainClient _siloGrainClient;
    private readonly DashboardCounters _counters;
    private readonly GrainProfilerOptions _grainProfilerOptions;
    private readonly TypeManifestOptions _typeManifestOptions;
    private readonly TimeSpan _updateInterval;
    private bool _isUpdating;
    private DateTime _startTime = DateTime.UtcNow;
    private DateTime _lastRefreshTime = DateTime.UtcNow;
    private DateTime _lastQuery = DateTime.UtcNow;
    private bool _isEnabled;

    public DashboardGrain(
        IOptions<DashboardOptions> options,
        IOptions<GrainProfilerOptions> grainProfilerOptions,
        IOptions<TypeManifestOptions> typeManifestOptions,
        ISiloDetailsProvider siloDetailsProvider,
        ISiloGrainClient siloGrainClient)
    {
        _siloDetailsProvider = siloDetailsProvider;
        _siloGrainClient = siloGrainClient;

        // Store the options to bypass the broadcase of the isEnabled flag.
        _grainProfilerOptions = grainProfilerOptions.Value;
        _typeManifestOptions = typeManifestOptions.Value;

        // Do not allow smaller timers than 1000ms = 1sec.
        _updateInterval = TimeSpan.FromMilliseconds(Math.Max(options.Value.CounterUpdateIntervalMs, 1000));

        // Make the history configurable.
        _counters = new DashboardCounters(options.Value.HistoryLength);

        _history = new TraceHistory(options.Value.HistoryLength);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _startTime = DateTime.UtcNow;

        if (!_grainProfilerOptions.TraceAlways)
        {
            var interval = TimeSpan.FromMinutes(1);

            this.RegisterGrainTimer(async x =>
            {
                var timeSinceLastQuery = DateTimeOffset.UtcNow - _lastQuery;

                if (timeSinceLastQuery > _grainProfilerOptions.DeactivationTime && _isEnabled)
                {
                    _isEnabled = false;
                    await BroadcaseEnabled();
                }
            }, new() { DueTime = interval, Period = interval, Interleave = true, KeepAlive = true });
        }

        return base.OnActivateAsync(cancellationToken);
    }

    private Task EnsureIsActive()
    {
        _lastQuery = DateTime.UtcNow;

        if (!_isEnabled)
        {
            _isEnabled = true;
            _ = BroadcaseEnabled();
        }

        return Task.CompletedTask;
    }

    private async Task BroadcaseEnabled()
    {
        if (_grainProfilerOptions.TraceAlways)
        {
            return;
        }

        var silos = await _siloDetailsProvider.GetSiloDetails();

        foreach (var siloAddress in silos.Select(x => x.SiloAddress))
        {
            await _siloGrainClient.GrainService(SiloAddress.FromParsableString(siloAddress)).Enable(_isEnabled);
        }
    }

    private async Task EnsureCountersAreUpToDate()
    {
        if (_isUpdating)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if ((now - _lastRefreshTime) < _updateInterval)
        {
            return;
        }

        _isUpdating = true;
        try
        {
            var metricsGrain = GrainFactory.GetGrain<IManagementGrain>(0);
            var activationCountTask = metricsGrain.GetTotalActivationCount();
            var simpleGrainStatsTask = metricsGrain.GetSimpleGrainStatistics();
            var siloDetailsTask = _siloDetailsProvider.GetSiloDetails();

            await Task.WhenAll(activationCountTask, simpleGrainStatsTask, siloDetailsTask);

            RecalculateCounters(activationCountTask.Result, siloDetailsTask.Result, simpleGrainStatsTask.Result);

            _lastRefreshTime = now;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    internal void RecalculateCounters(int activationCount, SiloDetails[] hosts,
        IList<SimpleGrainStatistic> simpleGrainStatistics)
    {
        _counters.TotalActivationCount = activationCount;

        _counters.TotalActiveHostCount = hosts.Count(x => x.SiloStatus == SiloStatus.Active);
        _counters.TotalActivationCountHistory =
            _counters.TotalActivationCountHistory.Enqueue(activationCount).Dequeue();
        _counters.TotalActiveHostCountHistory =
            _counters.TotalActiveHostCountHistory.Enqueue(_counters.TotalActiveHostCount).Dequeue();

        var elapsedTime = Math.Min((DateTime.UtcNow - _startTime).TotalSeconds, 100);

        _counters.Hosts = hosts;

        var aggregatedTotals = _history.GroupByGrainAndSilo().ToLookup(x => (x.Grain, x.SiloAddress));

        _counters.SimpleGrainStats = simpleGrainStatistics.Select(x =>
        {
            var grainName = TypeFormatter.Parse(x.GrainType);
            var siloAddress = x.SiloAddress.ToParsableString();

            var result = new SimpleGrainStatisticCounter
            {
                ActivationCount = x.ActivationCount,
                GrainType = grainName,
                SiloAddress = siloAddress,
                TotalSeconds = elapsedTime,
            };

            foreach (var item in aggregatedTotals[(grainName, siloAddress)])
            {
                result.TotalAwaitTime += item.ElapsedTime;
                result.TotalCalls += item.Count;
                result.TotalExceptions += item.ExceptionCount;
            }

            return result;
        }).ToArray();
    }

    public async Task<Immutable<DashboardCounters>> GetCounters()
    {
        await EnsureIsActive();
        await EnsureCountersAreUpToDate();

        return _counters.AsImmutable();
    }

    public async Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GetGrainTracing(
        string grain)
    {
        await EnsureIsActive();
        await EnsureCountersAreUpToDate();

        return _history.QueryGrain(grain).AsImmutable();
    }

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetClusterTracing()
    {
        await EnsureIsActive();
        await EnsureCountersAreUpToDate();

        return _history.QueryAll().AsImmutable();
    }

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetSiloTracing(string address)
    {
        await EnsureIsActive();
        await EnsureCountersAreUpToDate();

        return _history.QuerySilo(address).AsImmutable();
    }

    public async Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(int take)
    {
        await EnsureIsActive();
        await EnsureCountersAreUpToDate();

        var values = _history.AggregateByGrainMethod().ToList();

        GrainMethodAggregate[] GetTotalCalls()
        {
            return values.OrderByDescending(x => x.Count).Take(take).ToArray();
        }

        GrainMethodAggregate[] GetLatency()
        {
            return values.OrderByDescending(x => x.Count).Take(take).ToArray();
        }

        GrainMethodAggregate[] GetErrors()
        {
            return values.Where(x => x.ExceptionCount > 0 && x.Count > 0)
                .OrderByDescending(x => x.ExceptionCount / x.Count).Take(take).ToArray();
        }

        var result = new Dictionary<string, GrainMethodAggregate[]>
        {
            { "calls", GetTotalCalls() },
            { "latency", GetLatency() },
            { "errors", GetErrors() },
        };

        return result.AsImmutable();
    }

    public Task InitializeAsync() =>
        // just used to activate the grain
        Task.CompletedTask;

    public Task SubmitTracing(string siloAddress, Immutable<SiloGrainTraceEntry[]> grainTrace)
    {
        _history.Add(DateTime.UtcNow, siloAddress, grainTrace.Value);

        return Task.CompletedTask;
    }

    public async Task<Immutable<string>> GetGrainState(string id, string grainType)
    {
        var result = new ExpandoObject();

        try
        {
            var implementationType = _typeManifestOptions.InterfaceImplementations
                .FirstOrDefault(w => w.FullName.Equals(grainType));

            var mappedGrainId = GrainStateHelper.GetGrainId(id, implementationType);
            object grainId = mappedGrainId.Item1;
            string keyExtension = mappedGrainId.Item2;

            var propertiesAndFields = GrainStateHelper.GetPropertiesAndFieldsForGrainState(implementationType);

            var getGrainMethod = GrainStateHelper.GenerateGetGrainMethod(GrainFactory, grainId, keyExtension);

            var interfaceTypes = implementationType.GetInterfaces();

            foreach (var interfaceType in interfaceTypes)
            {
                try
                {
                    object[] grainMethodParameters;
                    if (string.IsNullOrWhiteSpace(keyExtension))
                        grainMethodParameters = new object[] { interfaceType, grainId };
                    else
                        grainMethodParameters = new object[] { interfaceType, grainId, keyExtension };

                    var grain = getGrainMethod.Invoke(GrainFactory, grainMethodParameters);

                    var methods = interfaceType.GetMethods().Where(w => w.GetParameters().Length == 0);

                    foreach (var method in methods)
                    {
                        try
                        {
                            if (method.ReturnType.IsAssignableTo(typeof(Task))
                                &&
                                (
                                    method.ReturnType.GetGenericArguments()
                                        .Any(a => propertiesAndFields.Any(f => f == a)
                                                  || method.Name == "GetState")
                                )
                               )
                            {
                                var task = (method.Invoke(grain, null) as Task);
                                var resultProperty = task.GetType().GetProperty("Result");

                                if (resultProperty == null)
                                    continue;

                                await task;

                                result.TryAdd(method.Name, resultProperty.GetValue(task));
                            }
                        }
                        catch
                        {
                            // Because we got all the interfaces some errors with boxing and unboxing may happen with invocations 
                        }
                    }
                }
                catch
                {
                    // Because we got all the interfaces some errors with boxing and unboxing may happen when try to get the grain
                }
            }
        }
        catch (Exception ex)
        {
            result.TryAdd("error", string.Concat(ex.Message, " - ", ex?.InnerException.Message));
        }

        return JsonSerializer.Serialize(result, options: new JsonSerializerOptions()
        {
            WriteIndented = true,
        }).AsImmutable();
    }

    public Task<Immutable<string[]>> GetGrainTypes()
    {
        return Task.FromResult(_typeManifestOptions.InterfaceImplementations
            .Select(s => s.FullName)
            .ToArray()
            .AsImmutable());
    }
}

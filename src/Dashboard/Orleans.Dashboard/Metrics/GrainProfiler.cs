using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Metrics.TypeFormatting;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Serialization.TypeSystem;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard.Metrics;

internal sealed class GrainProfiler(
    IGrainFactory grainFactory,
    ILogger<GrainProfiler> logger,
    ILocalSiloDetails localSiloDetails,
    IOptions<GrainProfilerOptions> options) : IGrainProfiler, ILifecycleParticipant<ISiloLifecycle>
{
    private ConcurrentDictionary<string, SiloGrainTraceEntry> _grainTrace = new();
    private Timer _timer;
    private string _siloAddress;
    private bool _isEnabled;
    private IDashboardGrain _dashboardGrain;

    public bool IsEnabled => options.Value.TraceAlways || _isEnabled;

    public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe<GrainProfiler>(ServiceLifecycleStage.Last, ct => OnStart(), ct => OnStop());

    private Task OnStart()
    {
        _timer = new Timer(ProcessStats, null, 1 * 1000, 1 * 1000);
        return Task.CompletedTask;
    }

    private Task OnStop()
    {
        _timer.Dispose();
        return Task.CompletedTask;
    }

    public void Track(double elapsedMs, Type grainType, [CallerMemberName] string methodName = null, bool failed = false)
    {
        ArgumentNullException.ThrowIfNull(grainType);

        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException("Method name cannot be null or empty.", nameof(methodName));
        }

        if (!IsEnabled)
        {
            return;
        }

        // This is the method that Orleans uses to convert a grain type into the grain type name when calling the GetSimpleGrainStatistics method
        var grainName = RuntimeTypeNameFormatter.Format(grainType);
        var grainMethodKey = $"{grainName}.{methodName}";

        var exceptionCount = (failed ? 1 : 0);

        _grainTrace.AddOrUpdate(grainMethodKey, _ =>
            new SiloGrainTraceEntry
            {
                Count = 1,
                ExceptionCount = exceptionCount,
                ElapsedTime = elapsedMs,
                Grain = grainName,
                Method = methodName
            },
        (_, last) =>
        {
            last.Count += 1;
            last.ElapsedTime += elapsedMs;

            if (failed)
            {
                last.ExceptionCount += exceptionCount;
            }

            return last;
        });
    }

    private void ProcessStats(object state)
    {
        if (!IsEnabled)
        {
            return;
        }

        var currentTrace = Interlocked.Exchange(ref _grainTrace, new ConcurrentDictionary<string, SiloGrainTraceEntry>());

        if (!currentTrace.IsEmpty)
        {
            _siloAddress ??= localSiloDetails.SiloAddress.ToParsableString();

            var items = currentTrace.Values.ToArray();

            foreach (var item in items)
            {
                item.Grain = TypeFormatter.Parse(item.Grain);
            }

            try
            {
                _dashboardGrain ??= grainFactory.GetGrain<IDashboardGrain>(0);

                _dashboardGrain.SubmitTracing(_siloAddress, items.AsImmutable()).Ignore();
            }
            catch (Exception ex)
            {
                logger.LogWarning(100001, ex, "Exception thrown sending tracing to dashboard grain");
            }
        }
    }

    public void Enable(bool enabled) => _isEnabled = enabled;
}

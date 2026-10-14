using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard.Implementation;

internal sealed partial class DashboardTelemetryExporter(
    ILocalSiloDetails localSiloDetails,
    ISiloGrainClient siloGrainClient,
    ILogger<DashboardTelemetryExporter> logger) : BaseExporter<Metric>
{
    // The dashboard's metric reader serializes Export calls.
    private readonly Dictionary<string, Value<double>> _metrics = [];
    private readonly ISiloGrainClient _siloGrainClient = siloGrainClient;
    private readonly ILogger<DashboardTelemetryExporter> _logger = logger;
    private readonly SiloAddress _siloAddress = localSiloDetails.SiloAddress;

    public readonly struct Value<T>
    {
        public readonly T Current;
        public readonly T Last;

        public Value(T value)
            : this(value, value)
        {
        }

        public Value(T last, T current)
        {
            Last = last;

            Current = current;
        }

        public Value<T> Update(T newValue) => new(Current, newValue);
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        var grain = _siloGrainClient.GrainService(_siloAddress);

        CollectMetricsFromBatch(batch);

        if (_metrics.Count == 0)
        {
            return ExportResult.Success;
        }

        var counters = new StatCounter[_metrics.Count];
        var countersIndex = 0;

        foreach (var (key, value) in _metrics)
        {
            counters[countersIndex] =
                new StatCounter(
                    key,
                    value.Current.ToString(CultureInfo.InvariantCulture),
                    ComputeDelta(value));

            countersIndex++;
        }

        grain.ReportCounters(counters.AsImmutable());
        return ExportResult.Success;
    }

    private void CollectMetricsFromBatch(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            switch (metric.MetricType)
            {
                case MetricType.LongSum:
                case MetricType.LongSumNonMonotonic:
                    CollectMetric(metric, p => p.GetSumLong());
                    break;
                case MetricType.DoubleSum:
                case MetricType.DoubleSumNonMonotonic:
                    CollectMetric(metric, p => p.GetSumDouble());
                    break;
                case MetricType.LongGauge:
                    CollectMetric(metric, p => p.GetGaugeLastValueLong());
                    break;
                case MetricType.DoubleGauge:
                    CollectMetric(metric, p => p.GetGaugeLastValueDouble());
                    break;
                case MetricType.Histogram:
                    CollectMetric(metric, p => p.GetHistogramSum());
                    break;
                default:
                    LogWarningIgnoringUnknownMetricType(_logger, metric.MetricType);
                    break;
            }
        }
    }

    private void CollectMetric(Metric metric, Func<MetricPoint, double> getValue)
    {
        // The dashboard displays one silo-wide value per instrument. Combine tagged
        // series before computing the change from the previous cumulative export.
        var value = 0d;
        foreach (var point in metric.GetMetricPoints())
        {
            value += getValue(point);
        }

        _metrics.TryGetValue(metric.Name, out var previous);
        _metrics[metric.Name] = previous.Update(value);
    }

    private static string ComputeDelta(Value<double> metric)
    {
        var delta = metric.Current - metric.Last;

        return delta.ToString(CultureInfo.InvariantCulture);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignoring unknown metric type {MetricType}"
    )]
    private static partial void LogWarningIgnoringUnknownMetricType(ILogger logger, MetricType metricType);
}

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class GrainTypeMetrics
{
    internal const string TagName = "grain_type";
    internal const string UnknownGrainType = "unknown";

    private readonly KeyValuePair<string, object?>[] _tags;
    private int _activationCount;
    private int _workingSetCount;

    internal GrainTypeMetrics(GrainType grainType)
    {
        GrainTypeTagValue = grainType.IsDefault ? UnknownGrainType : grainType.ToString();
        _tags = [new(TagName, GrainTypeTagValue)];
    }

    internal string GrainTypeTagValue { get; }

    internal Measurement<int> ActivationCount => new(Volatile.Read(ref _activationCount), _tags);
    internal Measurement<int> WorkingSetCount => new(Volatile.Read(ref _workingSetCount), _tags);

    internal void OnActivationAdded() => Interlocked.Increment(ref _activationCount);
    internal void OnActivationRemoved() => Interlocked.Decrement(ref _activationCount);
    internal void OnWorkingSetAdded() => Interlocked.Increment(ref _workingSetCount);
    internal void OnWorkingSetRemoved() => Interlocked.Decrement(ref _workingSetCount);
}

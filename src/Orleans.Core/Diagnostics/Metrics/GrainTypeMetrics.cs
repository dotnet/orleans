using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using MetricsTagList = System.Diagnostics.TagList;

namespace Orleans.Runtime;

internal sealed class GrainTypeMetrics
{
    internal const string TagName = "grain_type";
    internal const string UnknownGrainType = "unknown";
    internal const string KnownTagName = "grain_type_known";
    private static readonly object Known = true;
    private static readonly object Unknown = false;

    private readonly KeyValuePair<string, object?>[] _tags;
    private int _activationCount;
    private int _workingSetCount;

    internal GrainTypeMetrics(GrainType grainType)
    {
        GrainTypeTagValue = grainType.IsDefault ? UnknownGrainType : grainType.ToString();
        var tags = CreateTags(GrainTypeTagValue, isKnown: !grainType.IsDefault);
        _tags = new KeyValuePair<string, object?>[tags.Count];
        tags.CopyTo(_tags);
    }

    internal string GrainTypeTagValue { get; }

    internal static MetricsTagList CreateTags(string grainType, bool isKnown = true)
    {
        var tags = new MetricsTagList();
        AddTags(ref tags, grainType, isKnown);
        return tags;
    }

    internal static void AddTags(ref MetricsTagList tags, string grainType, bool isKnown = true)
    {
        tags.Add(TagName, grainType);
        if (grainType == UnknownGrainType)
        {
            tags.Add(KnownTagName, isKnown ? Known : Unknown);
        }
    }

    internal Measurement<int> ActivationCount => new(Volatile.Read(ref _activationCount), _tags);
    internal Measurement<int> WorkingSetCount => new(Volatile.Read(ref _workingSetCount), _tags);

    internal void OnActivationAdded() => Interlocked.Increment(ref _activationCount);
    internal void OnActivationRemoved() => Interlocked.Decrement(ref _activationCount);
    internal void OnWorkingSetAdded() => Interlocked.Increment(ref _workingSetCount);
    internal void OnWorkingSetRemoved() => Interlocked.Decrement(ref _workingSetCount);
}

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class ClusterManifestInstruments(OrleansInstruments instruments)
{
    private readonly Counter<long> _cacheLookups = instruments.Meter.CreateCounter<long>(InstrumentNames.MANIFEST_CACHE_LOOKUPS);
    private readonly Counter<long> _fallbacks = instruments.Meter.CreateCounter<long>(InstrumentNames.MANIFEST_FALLBACKS);
    private readonly Counter<long> _peerProbes = instruments.Meter.CreateCounter<long>(InstrumentNames.MANIFEST_PEER_PROBES);
    private readonly Counter<long> _peerRepairs = instruments.Meter.CreateCounter<long>(InstrumentNames.MANIFEST_PEER_REPAIRS);
    private readonly Histogram<double> _retrievalDuration = instruments.Meter.CreateHistogram<double>(InstrumentNames.MANIFEST_RETRIEVAL_DURATION, "ms");

    public bool RetrievalDurationEnabled => _retrievalDuration.Enabled;

    public void OnCacheLookup(bool hit, string source) => _cacheLookups.Add(
        1,
        new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"),
        new KeyValuePair<string, object?>("source", source));

    public void OnFallback(string reason) => _fallbacks.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void OnPeerProbe(string status) => _peerProbes.Add(1, new KeyValuePair<string, object?>("status", status));

    public void OnPeerRepair(int count) => _peerRepairs.Add(count);

    public void OnRetrievalCompleted(TimeSpan elapsed, string mode, string status)
    {
        if (_retrievalDuration.Enabled)
        {
            _retrievalDuration.Record(
                elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", mode),
                new KeyValuePair<string, object?>("status", status));
        }
    }
}

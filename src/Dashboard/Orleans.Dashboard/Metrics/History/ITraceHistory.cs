using System;
using System.Collections.Generic;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;

namespace Orleans.Dashboard.Metrics.History;

internal interface ITraceHistory
{
    void Add(DateTime time, string siloAddress, SiloGrainTraceEntry[] grainTrace);

    Dictionary<string, GrainTraceEntry> QueryAll();

    Dictionary<string, GrainTraceEntry> QuerySilo(string siloAddress);

    Dictionary<string, Dictionary<string, GrainTraceEntry>> QueryGrain(string grain);

    IEnumerable<TraceAggregate> GroupByGrainAndSilo();

    IEnumerable<GrainMethodAggregate> AggregateByGrainMethod();
}

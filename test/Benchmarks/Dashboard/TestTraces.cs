using Orleans.Dashboard.Model;
using System;

namespace Benchmarks.Dashboard
{
    internal sealed record TestTraces(DateTime Time, string Silo, SiloGrainTraceEntry[] Traces)
    {
    }
}

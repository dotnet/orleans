using System;
using Orleans.Dashboard.Model;

namespace Benchmarks.Dashboard
{
    internal sealed record TestTraces(DateTime Time, string Silo, SiloGrainTraceEntry[] Traces)
    {
    }
}

using System;

namespace LoadTestBase
{
    public class Callback : MarshalByRefObject
    {
        private readonly Func<string, int, int, int, int, Latency, bool> onReportBlock;

        public Callback(Func<string, int, int, int, int, Latency, bool> onReportBlock)
        {
            this.onReportBlock = onReportBlock;
        }

        public bool ReportBlock(string name, int nFailures, int pipelineSize, int nLate, int nBusy, Latency aggregateLatency)
        {
            return onReportBlock(name, nFailures, pipelineSize, nLate, nBusy, aggregateLatency);
        }
    }
}
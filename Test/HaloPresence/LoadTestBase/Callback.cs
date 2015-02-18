using System;

namespace LoadTestBase
{
    public class Callback : MarshalByRefObject
    {
        private readonly Func<string, int, int, int, int, int, Latency, bool> onReportBlock;

        public Callback(Func<string, int, int, int, int, int, Latency, bool> onReportBlock)
        {
            this.onReportBlock = onReportBlock;
        }

        public bool ReportBlock(string name, int nSuccess, int nFailures, int nLate, int nBusy, int pipelineSize, Latency aggregateLatency)
        {
            return onReportBlock(name, nSuccess, nFailures, nLate, nBusy, pipelineSize, aggregateLatency);
        }
    }
}
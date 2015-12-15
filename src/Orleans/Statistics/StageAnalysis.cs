using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Orleans.Runtime
{

    /*  Example of printout in logs:
    
    StageAnalysis=

    Stage: Runtime.IncomingMessageAgent.Application
      Measured average CPU per request:        0.067 ms
      Measured average Wall-clock per request: 0.068 ms
      Measured number of requests:             1777325 requests
      Estimated wait time:                     0.000 ms
      Suggested thread allocation:             2 threads  (rounded up from 1.136)
    Stage: Scheduler.WorkerPoolThread
      Measured average CPU per request:        0.153 ms
      Measured average Wall-clock per request: 0.160 ms
      Measured number of requests:             4404680 requests
      Estimated wait time:                     0.000 ms
      Suggested thread allocation:             7 threads  (rounded up from 6.386)
    Stage: Runtime.Messaging.GatewaySender.GatewaySiloSender
      Measured average CPU per request:        0.152 ms
      Measured average Wall-clock per request: 0.155 ms
      Measured number of requests:             92428 requests
      Estimated wait time:                     0.000 ms
      Suggested thread allocation:             1 threads  (rounded up from 0.133)
    Stage: Runtime.Messaging.SiloMessageSender.AppMsgsSender
      Measured average CPU per request:        0.034 ms
      Measured average Wall-clock per request: 0.125 ms
      Measured number of requests:             1815072 requests
      Estimated wait time:                     0.089 ms
      Suggested thread allocation:             2 threads  (rounded up from 1.765)

    CPU usage by thread type:
      0.415, Untracked
      0.359, Scheduler.WorkerPoolThread
      0.072, Untracked.ThreadPoolThread
      0.064, Runtime.IncomingMessageAgent.Application
      0.049, ThreadPoolThread
      0.033, Runtime.Messaging.SiloMessageSender.AppMsgsSender
      0.008, Runtime.Messaging.GatewaySender.GatewaySiloSender
      0.000, Scheduler.WorkerPoolThread.System
      0.000, Runtime.Messaging.SiloMessageSender.SystemSender
      0.000, Runtime.IncomingMessageAgent.System
      0.000, Runtime.Messaging.SiloMessageSender.PingSender
      0.000, Runtime.IncomingMessageAgent.Ping

    EndStageAnalysis
    */

    /// <summary>
    /// Stage analysis, one instance should exist in each Silo
    /// </summary>
    internal class StageAnalysis
    {
        private readonly double stableReadyTimeProportion;
        private readonly Dictionary<string, List<ThreadTrackingStatistic>> stageGroups;

        public StageAnalysis()
        {
            // Load test experiments suggested these parameter values
            stableReadyTimeProportion = 0.3;
            stageGroups = new Dictionary<string, List<ThreadTrackingStatistic>>();

            if (StatisticsCollector.CollectThreadTimeTrackingStats && StatisticsCollector.PerformStageAnalysis)
            {
                StringValueStatistic.FindOrCreate(StatisticNames.STAGE_ANALYSIS, StageAnalysisInfo);
            }
        }

        public void AddTracking(ThreadTrackingStatistic tts)
        {
            lock (stageGroups)
            {
                // we trim all thread numbers from thread name, so allow to group them.
                char[] toTrim = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '/', '_', '.' };
                string stageName = tts.Name.Trim(toTrim);
                List<ThreadTrackingStatistic> stageGroup;
                if (!stageGroups.TryGetValue(stageName, out stageGroup))
                {
                    stageGroup = new List<ThreadTrackingStatistic>();
                    stageGroups.Add(stageName, stageGroup);
                }
                stageGroup.Add(tts);
            }
        }

        // the interesting stages to print in stage analysis
        private static readonly List<string> stages = new List<string>()
        {
            "Runtime.IncomingMessageAgent.Application",
            "Scheduler.WorkerPoolThread",
            "Runtime.Messaging.GatewaySender.GatewaySiloSender",
            "Runtime.Messaging.SiloMessageSender.AppMsgsSender"
        };

        // stages where wait time is expected to be nonzero
        private static readonly HashSet<string> waitingStages = new HashSet<string>()
        {
            //stages[1], // if we know there is no waiting in the WorkerPoolThreads, we can remove it from waitingStages and get more accuarte measurements
            stages[2],
            stages[3]
        };

        private static readonly string firstStage = stages[0];

        private string StageAnalysisInfo()
        {
            try
            {
                lock (stageGroups)
                {
                    double cores = Environment.ProcessorCount;
                    Dictionary<string, double> cpuPerRequest = new Dictionary<string, double>(); // CPU time per request for each stage
                    Dictionary<string, double> wallClockPerRequest = new Dictionary<string, double>(); // Wallclock time per request for each stage
                    Dictionary<string, double> numberOfRequests = new Dictionary<string, double>(); // Number of requests for each stage
                    foreach (var keyVal in stageGroups)
                    {
                        string name = keyVal.Key;
                        if (GetNumberOfRequests(name) > 0)
                        {
                            cpuPerRequest.Add(name, GetCpuPerStagePerRequest(name));
                            wallClockPerRequest.Add(name, GetWallClockPerStagePerRequest(name));
                            numberOfRequests.Add(name, GetNumberOfRequests(name));
                        }
                    }
                    cpuPerRequest.Add("Untracked.ThreadPoolThread", GetCpuPerStagePerRequest("Untracked.ThreadPoolThread"));
                    numberOfRequests.Add("Untracked.ThreadPoolThread", GetNumberOfRequests("Untracked.ThreadPoolThread"));

                    double elapsedWallClock = GetMaxWallClock();
                    double elapsedCPUClock = GetTotalCPU();

                    // Idle time estimation
                    double untrackedProportionTime = 1 - elapsedCPUClock / (cores * elapsedWallClock);

                    // Ratio of wall clock per cpu time calculation
                    double sum = 0;
                    double num = 0;
                    foreach (var stage in wallClockPerRequest.Keys)
                    {
                        if (!waitingStages.Contains(stage))
                        {
                            double ratio = wallClockPerRequest[stage] / cpuPerRequest[stage] - 1;
                            sum += ratio * numberOfRequests[stage];
                            num += numberOfRequests[stage];
                        }
                    }
                    double avgRatio = sum / num;

                    // Wait time estimation - implementation of strategy 2 from the "local-throughput.pdf" "Coping with Practical Measurements". 
                    var waitTimes = new Dictionary<string, double>(); // Wait time per request for each stage
                    foreach (var stage in wallClockPerRequest.Keys)
                    {
                        waitTimes.Add(stage,
                            waitingStages.Contains(stage)
                                ? Math.Max(wallClockPerRequest[stage] - avgRatio*cpuPerRequest[stage] - cpuPerRequest[stage], 0)
                                : 0);
                    }

                    // CPU sum for denominator of final equation
                    double cpuSum = 0;
                    foreach (var stage in cpuPerRequest.Keys)
                    {
                        cpuSum += cpuPerRequest[stage] * numberOfRequests[stage];
                    }

                    // beta and lambda values
                    var beta = new Dictionary<string, double>();
                    var s = new Dictionary<string, double>();
                    var lambda = new Dictionary<string, double>();
                    foreach (var stage in wallClockPerRequest.Keys)
                    {
                        beta.Add(stage, cpuPerRequest[stage] / (cpuPerRequest[stage] + waitTimes[stage]));
                        s.Add(stage, 1000.0 / (cpuPerRequest[stage] + waitTimes[stage]));
                        lambda.Add(stage, 1000.0 * numberOfRequests[stage] / elapsedWallClock);
                    }

                    // Final equation thread allocation - implementation of theorem 2 from the "local-throughput.pdf" "Incorporating Ready Time". 
                    var throughputThreadAllocation = new Dictionary<string, double>(); // Thread allocation suggestion for each stage
                    foreach (var stage in wallClockPerRequest.Keys)
                    {
                        // cores is p
                        // numberOfRequests is q
                        // cpuPerRequest is x
                        // stableReadyTimeProportion is alpha
                        // waitTimes is w
                        throughputThreadAllocation.Add(stage, cores * numberOfRequests[stage] * (cpuPerRequest[stage] * (1 + stableReadyTimeProportion) + waitTimes[stage]) / cpuSum);
                    }

                    

                    double sum1 = 0;
                    foreach (var stage in s.Keys)
                        sum1 += lambda[stage]*beta[stage]/s[stage];

                    double sum2 = 0;
                    foreach (var stage in s.Keys)
                        sum2 += Math.Sqrt(lambda[stage]*beta[stage]/s[stage]);

                    var latencyThreadAllocation = new Dictionary<string, double>(); // Latency thread allocation suggestion for each stage
                    foreach (var stage in wallClockPerRequest.Keys)
                        latencyThreadAllocation.Add(stage, lambda[stage]/s[stage] + Math.Sqrt(lambda[stage])*(cores - sum1)/(Math.Sqrt(s[stage]*beta[stage])*sum2));

                    var latencyPenalizedThreadAllocationConst = new Dictionary<string, double>();
                    var latencyPenalizedThreadAllocationCoef = new Dictionary<string, double>();

                    foreach (var stage in wallClockPerRequest.Keys)
                    {
                        latencyPenalizedThreadAllocationConst.Add(stage, lambda[stage] / s[stage]);
                        latencyPenalizedThreadAllocationCoef.Add(stage, Math.Sqrt(lambda[stage] / (lambda[firstStage] * s[stage])));
                    }

                    double sum3 = 0;
                    foreach (var stage in s.Keys)
                        sum3 += beta[stage]*Math.Sqrt(lambda[stage]/s[stage]);

                    double zeta =  Math.Pow(sum3 / (cores - sum1), 2) / lambda[firstStage];
                    
                    var sb = new StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine("zeta:   " + zeta);
                    sb.AppendLine();
                    foreach (var stage in stages.Intersect(wallClockPerRequest.Keys))
                    {
                        sb.AppendLine("Stage: " + stage);
                        sb.AppendLine("  Measured average CPU per request:        " + cpuPerRequest[stage].ToString("F3") + " ms");
                        sb.AppendLine("  Measured average Wall-clock per request: " + wallClockPerRequest[stage].ToString("F3") + " ms");
                        sb.AppendLine("  Measured number of requests:             " + numberOfRequests[stage].ToString("F0") + " requests");
                        sb.AppendLine("  lambda:                                  " + lambda[stage].ToString("F3") + " arrival rate requests/sec");
                        sb.AppendLine("  s:                                       " + s[stage].ToString("F3") + " per thread service rate requests/sec");
                        sb.AppendLine("  beta:                                    " + beta[stage].ToString("F3") + " per thread CPU usage");
                        sb.AppendLine("  Estimated wait time:                     " + waitTimes[stage].ToString("F3") + " ms");
                        sb.AppendLine("  Throughput thread allocation:            " + Math.Ceiling(throughputThreadAllocation[stage]) + " threads  (rounded up from " + throughputThreadAllocation[stage].ToString("F3") + ")");
                        sb.AppendLine("  Latency thread allocation:               " + Math.Ceiling(latencyThreadAllocation[stage]) + " threads  (rounded up from " + latencyThreadAllocation[stage].ToString("F3") + ")");
                        sb.AppendLine("  Regularlized latency thread allocation:  " + latencyPenalizedThreadAllocationConst[stage].ToString("F3") + " + " + latencyPenalizedThreadAllocationCoef[stage].ToString("F3") + " / sqrt(eta) threads  (rounded this value up)");
                       
                    }

                    var cpuBreakdown = new Dictionary<string, double>();
                    foreach (var stage in cpuPerRequest.Keys)
                    {
                        double val = (numberOfRequests[stage] * cpuPerRequest[stage]) / (cores * elapsedWallClock);
                        cpuBreakdown.Add(stage == "ThreadPoolThread" ? "ThreadPoolThread.AsynchronousReceive" : stage, val);
                    }
                    cpuBreakdown.Add("Untracked", untrackedProportionTime);

                    sb.AppendLine();
                    sb.AppendLine("CPU usage by thread type:");
                    foreach (var v in cpuBreakdown.OrderBy(key => (-1*key.Value)))
                        sb.AppendLine("  " + v.Value.ToString("F3") + ", " + v.Key);

                    sb.AppendLine();
                    sb.Append("EndStageAnalysis");
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                return e + Environment.NewLine + e.StackTrace;
            }
        }

        /// <summary>
        /// get all cpu used by all types of threads
        /// </summary>
        /// <returns> milliseconds of total cpu time </returns>
        private double GetTotalCPU()
        {
            double total = 0;
            foreach (var keyVal in stageGroups)
                foreach (var statistics in keyVal.Value)
                    total += statistics.ExecutingCpuCycleTime.Elapsed.TotalMilliseconds;

            return total;
        }

        /// <summary>
        /// gets total wallclock which is the wallclock of the stage with maximum wallclock time
        /// </summary>
        private double GetMaxWallClock()
        {
            double maxTime = 0;
            foreach (var keyVal in stageGroups)
                foreach (var statistics in keyVal.Value)
                    maxTime = Math.Max(maxTime, statistics.ExecutingWallClockTime.Elapsed.TotalMilliseconds);

            maxTime -= 60 * 1000; // warmup time for grains needs to be subtracted
            return maxTime;
        }

        /// <summary>
        /// get number of requests for a stage
        /// </summary>
        /// <param name="stageName">name of a stage from thread tracking statistics</param>
        /// <returns>number of requests</returns>
        private double GetNumberOfRequests(string stageName)
        {
            if (stageName == "Untracked.ThreadPoolThread")
                return 1;

            double num = 0;
            if (!stageGroups.ContainsKey(stageName))
                return 0;

            foreach (var tts in stageGroups[stageName])
                num += tts.NumRequests;

            return num;
        }

        /// <summary>
        /// get wall clock time for a request of a stage
        /// </summary>
        /// <param name="stageName">name of a stage from thread tracking statistics</param>
        /// <returns>average milliseconds of wallclock time per request</returns>
        private double GetWallClockPerStagePerRequest(string stageName)
        {
            double sum = 0;
            if (!stageGroups.ContainsKey(stageName))
                return 0;

            foreach (var statistics in stageGroups[stageName])
                if (stageName == "ThreadPoolThread")
                {
                    sum += statistics.ProcessingWallClockTime.Elapsed.TotalMilliseconds;
                }
                else
                {
                    sum += statistics.ProcessingWallClockTime.Elapsed.TotalMilliseconds;

                    // We need to add the pure Take time, since in the GetCpuPerStagePerRequest we includes both processingCPUCycleTime and the Take time. 
                    TimeSpan takeCPUCycles = statistics.ExecutingCpuCycleTime.Elapsed -
                                             statistics.ProcessingCpuCycleTime.Elapsed;
                    sum += takeCPUCycles.TotalMilliseconds;
                }

            return sum / GetNumberOfRequests(stageName);
        }

        /// <summary>
        /// get cpu time for a request of a stage
        /// </summary>
        /// <param name="stageName">name of a stage from thread tracking statistics</param>
        /// <returns>average milliseconds of cpu time per request</returns>
        private double GetCpuPerStagePerRequest(string stageName)
        {
            double sum = 0;
            if (stageName == "Untracked.ThreadPoolThread")
            {
                foreach (var statistics in stageGroups["ThreadPoolThread"])
                {
                    sum += statistics.ExecutingCpuCycleTime.Elapsed.TotalMilliseconds;
                    sum -= statistics.ProcessingCpuCycleTime.Elapsed.TotalMilliseconds;
                }
                return sum;
            }

            if (!stageGroups.ContainsKey(stageName))
                return 0;

            foreach (var statistics in stageGroups[stageName])
            {
                if (stageName == "ThreadPoolThread")
                {
                    sum += statistics.ProcessingCpuCycleTime.Elapsed.TotalMilliseconds;
                }
                else
                {
                    // this includes both processingCPUCycleTime and the Take time.
                    sum += statistics.ExecutingCpuCycleTime.Elapsed.TotalMilliseconds;
                }
            }
            return sum / GetNumberOfRequests(stageName);
        }
    }
}

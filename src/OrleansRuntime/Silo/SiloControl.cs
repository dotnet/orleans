/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal class SiloControl : SystemTarget, ISiloControl
    {
        private readonly Silo silo;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("SiloControl", TraceLogger.LoggerType.Runtime);

        public SiloControl(Silo silo)
            : base(Constants.SiloControlId, silo.SiloAddress)
        {
            this.silo = silo;
        }

        #region Implementation of ISiloControl

        public Task Ping(string message)
        {
            logger.Info("Ping");
            return TaskDone.Done;
        }

        public Task SetSystemLogLevel(int traceLevel)
        {
            var newTraceLevel = (Logger.Severity)traceLevel;
            logger.Info("SetSystemLogLevel={0}", newTraceLevel);
            TraceLogger.SetRuntimeLogLevel(newTraceLevel);
            silo.LocalConfig.DefaultTraceLevel = newTraceLevel;
            return TaskDone.Done;
        }

        public Task SetAppLogLevel(int traceLevel)
        {
            var newTraceLevel = (Logger.Severity)traceLevel;
            logger.Info("SetAppLogLevel={0}", newTraceLevel);
            TraceLogger.SetAppLogLevel(newTraceLevel);
            return TaskDone.Done;
        }

        public Task SetLogLevel(string logName, int traceLevel)
        {
            var newTraceLevel = (Logger.Severity)traceLevel;
            logger.Info("SetLogLevel[{0}]={1}", logName, newTraceLevel);
            TraceLogger log = TraceLogger.FindLogger(logName);
            
            if (log == null) throw new ArgumentException(string.Format("Logger {0} not found", logName));
            
            log.SetSeverityLevel(newTraceLevel);
            return TaskDone.Done;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect")]
        public Task ForceGarbageCollection()
        {
            logger.Info("ForceGarbageCollection");
            GC.Collect();
            return TaskDone.Done;
        }

        public Task ForceActivationCollection(TimeSpan ageLimit)
        {
            logger.Info("ForceActivationCollection");
            return InsideRuntimeClient.Current.Catalog.CollectActivations(ageLimit);
        }

        public Task ForceRuntimeStatisticsCollection()
        {
            if (logger.IsVerbose) logger.Verbose("ForceRuntimeStatisticsCollection");
            return DeploymentLoadPublisher.Instance.RefreshStatistics();
        }

        public Task<SiloRuntimeStatistics> GetRuntimeStatistics()
        {
            if (logger.IsVerbose) logger.Verbose("GetRuntimeStatistics");
            return Task.FromResult(new SiloRuntimeStatistics(silo.Metrics, DateTime.UtcNow));
        }

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics()
        {
            logger.Info("GetGrainStatistics");
            return Task.FromResult( InsideRuntimeClient.Current.Catalog.GetGrainStatistics());
        }

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics()
        {
            logger.Info("GetSimpleGrainStatistics");
            return Task.FromResult( InsideRuntimeClient.Current.Catalog.GetSimpleGrainStatistics().Select(p =>
                new SimpleGrainStatistic { SiloAddress = silo.SiloAddress, GrainType = p.Key, ActivationCount = (int)p.Value }).ToArray());
        }

        public Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId)
        {
            logger.Info("DetailedGrainReport for grain id {0}", grainId);
            return Task.FromResult( InsideRuntimeClient.Current.Catalog.GetDetailedGrainReport(grainId));
        }

        public Task UpdateConfiguration(string configuration)
        {
            logger.Info("UpdateConfiguration with {0}", configuration);
            silo.OrleansConfig.Update(configuration);
            logger.Info(ErrorCode.Runtime_Error_100318, "UpdateConfiguration - new config is now {0}", silo.OrleansConfig.ToString(silo.Name));
            return TaskDone.Done;
        }

        public Task<int> GetActivationCount()
        {
            return Task.FromResult(InsideRuntimeClient.Current.Catalog.ActivationCount);
        }

        #endregion
    }
}

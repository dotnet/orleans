using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class SingleTaskAsynchAgent : AsynchAgent
    {
        protected SingleTaskAsynchAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory) : base(nameSuffix, executorService, loggerFactory)
        {
        }

        protected SingleTaskAsynchAgent(ExecutorService executorService, ILoggerFactory loggerFactory) : base(executorService, loggerFactory)
        {
        }

        public override void OnStart()
        {
            executor.QueueWorkItem(_ => AgentThreadProc());
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void AgentThreadProc()
        {
            var agent = this;
            if (agent == null)
            {
                throw new InvalidOperationException("Agent thread started with incorrect parameter type");
            }

            try
            {
                LogStatus(agent.Log, "Starting AsyncAgent {0} on managed thread {1}", agent.Name, Thread.CurrentThread.ManagedThreadId);
                CounterStatistic.SetOrleansManagedThread(); // do it before using CounterStatistic.
                CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, agent.type)).Increment();
                CounterStatistic.FindOrCreate(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_TOTAL_THREADS_CREATED).Increment();
                agent.Run();
            }
            catch (Exception exc)
            {
                if (agent.State == ThreadState.Running) // If we're stopping, ignore exceptions
                {
                    var log = agent.Log;
                    switch (agent.OnFault)
                    {
                        case FaultBehavior.CrashOnFault:
                            Console.WriteLine(
                                "The {0} agent has thrown an unhandled exception, {1}. The process will be terminated.",
                                agent.Name, exc);
                            log.Error(ErrorCode.Runtime_Error_100023,
                                "AsynchAgent Run method has thrown an unhandled exception. The process will be terminated.",
                                exc);
                            log.Fail(ErrorCode.Runtime_Error_100024, "Terminating process because of an unhandled exception caught in AsynchAgent.Run.");
                            break;
                        case FaultBehavior.IgnoreFault:
                            log.Error(ErrorCode.Runtime_Error_100025, "AsynchAgent Run method has thrown an unhandled exception. The agent will exit.",
                                exc);
                            agent.State = ThreadState.Stopped;
                            break;
                        case FaultBehavior.RestartOnFault:
                            log.Error(ErrorCode.Runtime_Error_100026,
                                "AsynchAgent Run method has thrown an unhandled exception. The agent will be restarted.",
                                exc);
                            agent.State = ThreadState.Stopped;
                            try
                            {
                                agent.Start();
                            }
                            catch (Exception ex)
                            {
                                log.Error(ErrorCode.Runtime_Error_100027, "Unable to restart AsynchAgent", ex);
                                agent.State = ThreadState.Stopped;
                            }
                            break;
                    }
                }
            }
            finally
            {
                CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, agent.type)).DecrementBy(1);
                agent.Log.Info(ErrorCode.Runtime_Error_100328, "Stopping AsyncAgent {0} that runs on managed thread {1}", agent.Name, Thread.CurrentThread.ManagedThreadId);
            }
        }

        protected abstract void Run();

        private static void LogStatus(ILogger log, string msg, params object[] args)
        {
            if (IsStarting)
            {
                // Reduce log noise during silo startup
                if (log.IsEnabled(LogLevel.Debug)) log.Debug(msg, args);
            }
            else
            {
                // Changes in agent threads during all operations aside for initial creation are usually important diag events.
                log.Info(msg, args);
            }
        }
    }
}
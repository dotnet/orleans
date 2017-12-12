using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class AsynchAgent : IDisposable
    {
        public enum FaultBehavior
        {
            CrashOnFault,   // Crash the process if the agent faults
            RestartOnFault, // Restart the agent if it faults
            IgnoreFault     // Allow the agent to stop if it faults, but take no other action (other than logging)
        }

        protected readonly ExecutorService executorService;
        protected CancellationTokenSource Cts;
        protected object Lockable;
        protected ILogger Log;
        private readonly string type;
        protected FaultBehavior OnFault;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadState State { get; private set; }
        internal string Name { get; private set; }

        protected AsynchAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory)
        {
            this.executorService = executorService;
            Cts = new CancellationTokenSource();
            var thisType = GetType();

            type = thisType.Namespace + "." + thisType.Name;
            if (type.StartsWith("Orleans.", StringComparison.Ordinal))
            {
                type = type.Substring(8);
            }
            if (!string.IsNullOrEmpty(nameSuffix))
            {
                Name = type + "/" + nameSuffix;
            }
            else
            {
                Name = type;
            }

            Lockable = new object();
            State = ThreadState.Unstarted;
            OnFault = FaultBehavior.IgnoreFault;
            Log = loggerFactory.CreateLogger(Name);

            AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
        }

        protected AsynchAgent(ExecutorService executorService, ILoggerFactory loggerFactory)
            : this(null, executorService, loggerFactory)
        {
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            try
            {
                if (State != ThreadState.Stopped)
                {
                    Stop();
                }
            }
            catch (Exception exc)
            {
                // ignore. Just make sure DomainUnload handler does not throw.
                Log.Debug("Ignoring error during Stop: {0}", exc);
            }
        }

        public virtual void Start()
        {
            lock (Lockable)
            {
                if (State == ThreadState.Running)
                {
                    return;
                }

                if (State == ThreadState.Stopped)
                {
                    Cts = new CancellationTokenSource();
                }

                executorService.RunTask(new AsynchAgentTask(() => AgentThreadProc(this), Name));
                State = ThreadState.Running;
            }
            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("Started asynch agent " + this.Name);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public virtual void Stop()
        {
            try
            {
                lock (Lockable)
                {
                    if (State == ThreadState.Running)
                    {
                        State = ThreadState.StopRequested;
                        Cts.Cancel();
                        State = ThreadState.Stopped;
                    }
                }

                AppDomain.CurrentDomain.DomainUnload -= CurrentDomain_DomainUnload;
            }
            catch (Exception exc)
            {
                // ignore. Just make sure stop does not throw.
                Log.Debug("Ignoring error during Stop: {0}", exc);
            }
            Log.Debug("Stopped agent");
        }
        
        protected abstract void Run();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static void AgentThreadProc(Object obj)
        {
            var agent = obj as AsynchAgent;
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

#region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            if (Cts != null)
            {
                Cts.Dispose();
                Cts = null;
            }
        }

#endregion

        public override string ToString()
        {
            return Name;
        }

        internal static bool IsStarting { get; set; }

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
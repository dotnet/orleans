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
        protected IExecutor executor;
        protected CancellationTokenSource Cts;
        protected object Lockable;
        protected ILogger Log;
        protected readonly string type;
        protected FaultBehavior OnFault;
        protected bool disposed;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadState State { get; protected set; }
        internal string Name { get; private set; }

        protected AsynchAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory)
        {
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

            this.executorService = executorService;
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
            ThrowIfDisposed();
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

                EnsureExecutorInitialized();
                OnStart();
                State = ThreadState.Running;
            }

            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("Started asynch agent " + this.Name);
        }

        public virtual void OnStart() { }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public virtual void Stop()
        {
            try
            {
                ThrowIfDisposed();
                lock (Lockable)
                {
                    if (State == ThreadState.Running)
                    {
                        State = ThreadState.StopRequested;
                        Cts.Cancel();
                        executor = null;
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

#region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || disposed) return;

            if (Cts != null)
            {
                Cts.Dispose();
                Cts = null;
            }

            disposed = true;
        }

#endregion

        public override string ToString()
        {
            return Name;
        }

        internal static bool IsStarting { get; set; }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("Cannot access a disposed AsynchAgent"); 
            }
        }

        private void EnsureExecutorInitialized()
        {
            if (executor == null)
            {
                executor = executorService.GetExecutor(new GetExecutorRequest(GetType(), Name, Cts));
            }
        }
    }
}
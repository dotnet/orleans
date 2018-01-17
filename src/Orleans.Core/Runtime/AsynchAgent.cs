using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Threading;

namespace Orleans.Runtime
{
    internal abstract class AsynchAgent : IHealthCheckable, IDisposable
    {
        public enum FaultBehavior
        {
            CrashOnFault,   // Crash the process if the agent faults
            RestartOnFault, // Restart the agent if it faults
            IgnoreFault     // Allow the agent to stop if it faults, but take no other action (other than logging)
        }

        private readonly ExecutorFaultHandler executorFaultHandler;

        protected readonly ExecutorService executorService;
        protected ThreadPoolExecutor executor;
        protected CancellationTokenSource Cts;
        protected object Lockable;
        protected ILogger Log;
        protected ILoggerFactory loggerFactory;
        protected readonly string type;
        protected FaultBehavior OnFault;
        protected bool disposed;

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

            this.loggerFactory = loggerFactory;
            this.Log = loggerFactory.CreateLogger(Name);
            this.executorService = executorService;
            this.executorFaultHandler = new ExecutorFaultHandler(this);
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

                AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;
                LogStatus(Log, "Starting AsyncAgent {0} on managed thread {1}", Name, Thread.CurrentThread.ManagedThreadId);
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

        public bool CheckHealth(DateTime lastCheckTime)
        {
            return executor.CheckHealth(lastCheckTime);
        }

        internal static bool IsStarting { get; set; }

        protected virtual ThreadPoolExecutorOptions.Builder ExecutorOptionsBuilder =>
            new ThreadPoolExecutorOptions.Builder(Name, GetType(), Cts, loggerFactory).WithExceptionFilters(executorFaultHandler);

        private sealed class ExecutorFaultHandler : ExecutionExceptionFilter
        {
            private readonly AsynchAgent agent;

            public ExecutorFaultHandler(AsynchAgent agent)
            {
                this.agent = agent;
            }

            public override bool ExceptionHandler(Exception ex, Threading.ExecutionContext context)
            {
                context.CancellationTokenSource.Cancel();
                agent.HandleFault(ex);
                return true;
            }
        }

        protected void HandleFault(Exception ex)
        {
            State = ThreadState.Stopped;
            if (ex is ThreadAbortException)
            {
                return;
            }

            LogExecutorError(ex);

            if (OnFault == FaultBehavior.RestartOnFault && !Cts.IsCancellationRequested)
            {
                try
                {
                    Start();
                }
                catch (Exception exc)
                {
                    Log.Error(ErrorCode.Runtime_Error_100027, "Unable to restart AsynchAgent", exc);
                    State = ThreadState.Stopped;
                }
            }
        }

        private void EnsureExecutorInitialized()
        {
            if (executor == null)
            {
                executor = executorService.GetExecutor(ExecutorOptionsBuilder.Options);
            }
        }

        private void LogExecutorError(Exception exc)
        {
            var logMessagePrefix = $"Asynch agent {Name} encountered unexpected exception";
            switch (OnFault)
            {
                case FaultBehavior.CrashOnFault:
                    var logMessage = $"{logMessagePrefix} The process will be terminated.";
                    Console.WriteLine(logMessage, exc);
                    Log.Error(ErrorCode.Runtime_Error_100023, logMessage, exc);
                    Log.Fail(ErrorCode.Runtime_Error_100024, logMessage);
                    break;
                case FaultBehavior.IgnoreFault:
                    Log.Error(ErrorCode.Runtime_Error_100025, $"{logMessagePrefix} The executor will exit.", exc);
                    break;
                case FaultBehavior.RestartOnFault:
                    Log.Error(ErrorCode.Runtime_Error_100026, $"{logMessagePrefix} The Stage will be restarted.", exc);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

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

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("Cannot access disposed AsynchAgent");
            }
        }
    }
}
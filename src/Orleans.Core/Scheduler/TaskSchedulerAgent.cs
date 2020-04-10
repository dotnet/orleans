using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class TaskSchedulerAgent : IDisposable
    {
        public enum FaultBehavior
        {
            CrashOnFault,   // Crash the process if the agent faults
            RestartOnFault, // Restart the agent if it faults
            IgnoreFault     // Allow the agent to stop if it faults, but take no other action (other than logging)
        }

        public enum AgentState
        {
            Stopped,
            Running,
            StopRequested
        }
               
        protected CancellationTokenSource Cts;
        protected object Lockable;
        protected ILogger Log;
        protected FaultBehavior OnFault;
        protected bool disposed;

        public AgentState State { get; private set; }
        internal string Name { get; private set; }

        protected TaskSchedulerAgent(ILoggerFactory loggerFactory)
        {
            Cts = new CancellationTokenSource();

            this.Log = loggerFactory.CreateLogger(this.GetType());
            var typeName = GetType().FullName;
            if (typeName.StartsWith("Orleans.", StringComparison.Ordinal))
            {
                typeName = typeName.Substring(8);
            }

            Name = typeName;

            Lockable = new object();
            OnFault = FaultBehavior.IgnoreFault;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            try
            {
                if (State != AgentState.Stopped)
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
                if (State == AgentState.Running)
                {
                    return;
                }

                if (State == AgentState.Stopped)
                {
                    Cts = new CancellationTokenSource();
                }

                AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;
                LogStatus(Log, "Starting AsyncAgent {0} on managed thread {1}", Name, Thread.CurrentThread.ManagedThreadId);
                State = AgentState.Running;
            }

            Task.Run(() => this.StartAsync()).Ignore();

            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("Started asynch agent " + this.Name);
        }

        private async Task StartAsync()
        {
            var handled = false;
            try
            {
                await this.Run();
            }
            catch (Exception exception)
            {
                this.HandleFault(exception);
                handled = true;
            }
            finally
            {
                if (!handled)
                {
                    if (this.OnFault == FaultBehavior.RestartOnFault && !this.Cts.IsCancellationRequested)
                    {
                        try
                        {
                            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("Run completed on agent " + this.Name + " - restarting");
                            this.Start();
                        }
                        catch (Exception exc)
                        {
                            this.Log.Error(ErrorCode.Runtime_Error_100027, "Unable to restart AsynchAgent", exc);
                            this.State = AgentState.Stopped;
                        }
                    }
                }
            }
        }

        protected abstract Task Run();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public virtual void Stop()
        {
            try
            {
                ThrowIfDisposed();
                lock (Lockable)
                {
                    if (State == AgentState.Running)
                    {
                        State = AgentState.StopRequested;
                        Cts.Cancel();
                        State = AgentState.Stopped;
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

        public override string ToString()
        {
            return Name;
        }
        
        internal static bool IsStarting { get; set; }
        
        /// <summary>
        /// Handles fault
        /// </summary>
        /// <param name="ex"></param>
        /// <returns>false agent has been stopped</returns>
        protected bool HandleFault(Exception ex)
        {
            if (State == AgentState.StopRequested)
            {
                return false;
            }
            else
            {
                State = AgentState.Stopped;
            }

            if (ex is ThreadAbortException)
            {
                return false;
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
                    State = AgentState.Stopped;
                }
            }

            return State != AgentState.Stopped;
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
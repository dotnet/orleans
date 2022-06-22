using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class TaskSchedulerAgent : IDisposable
    {
        protected enum FaultBehavior
        {
            RestartOnFault, // Restart the agent if it faults
            IgnoreFault     // Allow the agent to stop if it faults, but take no other action (other than logging)
        }

        private enum AgentState
        {
            Stopped,
            Running,
            StopRequested
        }
               
        protected CancellationTokenSource Cts { get; private set; }
        private readonly object lockable;
        protected ILogger Log { get; }
        protected FaultBehavior OnFault { get; set; }
        private bool disposed;

        private AgentState State { get; set; }
        private string Name { get; set; }

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

            lockable = new object();
            OnFault = FaultBehavior.IgnoreFault;
        }

        public virtual void Start()
        {
            ThrowIfDisposed();
            lock (lockable)
            {
                if (State == AgentState.Running)
                {
                    return;
                }

                if (State == AgentState.Stopped)
                {
                    Cts = new CancellationTokenSource();
                }

                State = AgentState.Running;
            }

            Task.Run(() => this.StartAsync()).Ignore();

            if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("Started asynch agent {Name}", this.Name);
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
                            if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug("Run completed on agent {Name} - restarting", Name);
                            this.Start();
                        }
                        catch (Exception exc)
                        {
                            this.Log.LogError((int)ErrorCode.Runtime_Error_100027, exc, $"Unable to restart {nameof(TaskSchedulerAgent)}");
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
                lock (lockable)
                {
                    if (State == AgentState.Running)
                    {
                        State = AgentState.StopRequested;
                        Cts.Cancel();
                        State = AgentState.Stopped;
                    }
                }
            }
            catch (Exception exc)
            {
                if (Log.IsEnabled(LogLevel.Debug))
                {
                    // ignore. Just make sure stop does not throw.
                    Log.LogDebug(exc, "Ignoring error during Stop");
                }
            }

            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Stopped agent");
            }
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
                    Log.LogError((int)ErrorCode.Runtime_Error_100027, exc, $"Unable to restart {nameof(TaskSchedulerAgent)}");
                    State = AgentState.Stopped;
                }
            }

            return State != AgentState.Stopped;
        }
        
        private void LogExecutorError(Exception exc)
        {
            switch (OnFault)
            {
                case FaultBehavior.IgnoreFault:
                    Log.LogError((int)ErrorCode.Runtime_Error_100025, exc, "Asynch agent {Name} encountered unexpected exception. The executor will exit.", Name);
                    break;
                case FaultBehavior.RestartOnFault:
                    Log.LogError((int)ErrorCode.Runtime_Error_100026, exc, "Asynch agent {Name} encountered unexpected exception. The Stage will be restarted.", Name);
                    break;
                default:
                    throw new NotImplementedException();
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
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

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("Cannot access disposed AsynchAgent");
            }
        }
    }
}
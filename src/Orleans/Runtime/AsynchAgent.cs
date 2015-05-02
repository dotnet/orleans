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
using System.Text;
using System.Threading;
using Orleans.Runtime;

namespace Orleans.Runtime
{
    internal abstract class AsynchAgent : MarshalByRefObject, IDisposable
    {
        public enum FaultBehavior
        {
            CrashOnFault,   // Crash the process if the agent faults
            RestartOnFault, // Restart the agent if it faults
            IgnoreFault     // Allow the agent to stop if it faults, but take no other action (other than logging)
        }

        private Thread t;
        protected CancellationTokenSource Cts;
        protected object Lockable;
        protected TraceLogger Log;
        private readonly string type;
        protected FaultBehavior OnFault;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        static protected readonly Dictionary<Type, int> SequenceNumbers = new Dictionary<Type, int>();
        static private readonly object classLockable = new object();

        public ThreadState State { get; private set; }
        internal string Name { get; private set; }
        internal int ManagedThreadId { get { return t==null ? -1 : t.ManagedThreadId;  } } 

        protected AsynchAgent(string nameSuffix)
        {
            Cts = new CancellationTokenSource();
            var thisType = GetType();
            int n = 0;

            lock (classLockable)
            {
                SequenceNumbers.TryGetValue(thisType, out n);
                n++;
                SequenceNumbers[thisType] = n;
            }
            
            type = thisType.Namespace + "." + thisType.Name;
            if (type.StartsWith("Orleans.", StringComparison.Ordinal))
            {
                type = type.Substring(8);
            }
            if (!string.IsNullOrEmpty(nameSuffix))
            {
                Name = type + "." + nameSuffix + "/" + n;
            }
            else
            {
                Name = type + "/" + n;
            }

            Lockable = new object();
            State = ThreadState.Unstarted;
            OnFault = FaultBehavior.IgnoreFault;
            Log = TraceLogger.GetLogger(Name, TraceLogger.LoggerType.Runtime);
            AppDomain.CurrentDomain.DomainUnload += new EventHandler(CurrentDomain_DomainUnload);

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif

            t = new Thread(AgentThreadProc) { IsBackground = true, Name = this.Name };
        }

        protected AsynchAgent() : this(null)
        {
        }

        protected int GetThreadTypeSequenceNumber()
        {
            var thisType = GetType();
            int n;

            lock (classLockable)
            {
                SequenceNumbers.TryGetValue(thisType, out n);
            }
            return n;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            try
            {
                Stop();
            }
            catch (Exception exc)
            {
                // ignore. Just make sure DomainUnload handler does not throw.
                Log.Verbose("Ignoring error during Stop: {0}", exc);
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
                    t = new Thread(AgentThreadProc) { IsBackground = true, Name = this.Name };
                }

                t.Start(this);
                State = ThreadState.Running;
            }
            if(Log.IsVerbose) Log.Verbose("Started asynch agent " + this.Name);
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
            }
            catch (Exception exc)
            {
                // ignore. Just make sure stop does not throw.
                Log.Verbose("Ignoring error during Stop: {0}", exc);
            }
            Log.Verbose("Stopped agent");
        }

        public void Abort(object stateInfo)
        {
            if(t!=null)
                t.Abort(stateInfo);
        }

        protected abstract void Run();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static void AgentThreadProc(Object obj)
        {
            var agent = obj as AsynchAgent;
            if (agent == null)
            {
                var log = TraceLogger.GetLogger("RuntimeCore.AsynchAgent");
                log.Error(ErrorCode.Runtime_Error_100022, "Agent thread started with incorrect parameter type");
                return;
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

        private static void LogStatus(TraceLogger log, string msg, params object[] args)
        {
            if (SystemStatus.Current.Equals(SystemStatus.Creating))
            {
                // Reduce log noise during silo startup
                if (log.IsVerbose) log.Verbose(msg, args);
            }
            else
            {
                // Changes in agent threads during all operations aside for initial creation are usually important diag events.
                log.Info(msg, args);
            }
        }
    }
}

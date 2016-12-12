using Orleans.Concurrency;
using Orleans.MultiCluster;
using Orleans.LogConsistency;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Storage;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// A base class for log-consistent grains using standard event-sourcing terminology.
    /// All operations are reentrancy-safe.
    /// <typeparam name="TGrainState">The type for the grain state, i.e. the aggregate view of the event log.</typeparam>
    /// </summary>
    public abstract class JournaledGrain<TGrainState> : JournaledGrain<TGrainState, object>
        where TGrainState : class, new()
    { }


    /// <summary>
    /// A base class for log-consistent grains using standard event-sourcing terminology.
    /// All operations are reentrancy-safe.
    /// <typeparam name="TGrainState">The type for the grain state, i.e. the aggregate view of the event log.</typeparam>
    /// <typeparam name="TEventBase">The common base class for the events</typeparam>
    /// </summary>
    public abstract class JournaledGrain<TGrainState,TEventBase> :
        LogConsistentGrainBase<TGrainState>,
        ILogConsistentGrain,
        IProtocolParticipant,
        ILogViewAdaptorHost<TGrainState, TEventBase>
        where TGrainState : class, new()
        where TEventBase: class
    {
        protected JournaledGrain() { }

        /// <summary>
        /// Raise an event.
        /// </summary>
        /// <param name="event">Event to raise</param>
        /// <returns></returns>
        protected virtual void RaiseEvent<TEvent>(TEvent @event) 
            where TEvent : TEventBase
        {
            if (@event == null) throw new ArgumentNullException("event");

            LogViewAdaptor.Submit(@event);
        }

        /// <summary>
        /// Raise multiple events, as an atomic sequence.
        /// </summary>
        /// <param name="events">Events to raise</param>
        /// <returns></returns>
        protected virtual void RaiseEvents<TEvent>(IEnumerable<TEvent> events) 
            where TEvent : TEventBase
        {
            if (events == null) throw new ArgumentNullException("events");

            LogViewAdaptor.SubmitRange((IEnumerable<TEventBase>) events);
        }


        /// <summary>
        /// Raise an event conditionally. 
        /// Succeeds only if there are no conflicts, that is, no other events were raised in the meantime.
        /// </summary>
        /// <param name="event">Event to raise</param>
        /// <returns>true if successful, false if there was a conflict.</returns>
        protected virtual Task<bool> RaiseConditionalEvent<TEvent>(TEvent @event)
            where TEvent : TEventBase
        {
            if (@event == null) throw new ArgumentNullException("event");

            return LogViewAdaptor.TryAppend(@event);
        }


        /// <summary>
        /// Raise multiple events, as an atomic sequence, conditionally. 
        /// Succeeds only if there are no conflicts, that is, no other events were raised in the meantime.
        /// </summary>
        /// <param name="events">Events to raise</param>
        /// <returns>true if successful, false if there was a conflict.</returns>
        protected virtual Task<bool> RaiseConditionalEvents<TEvent>(IEnumerable<TEvent> events)
            where TEvent : TEventBase
        {
            if (events == null) throw new ArgumentNullException("events");
            return LogViewAdaptor.TryAppendRange((IEnumerable<TEventBase>) events);
        }

        /// <summary>
        /// The current state (includes both confirmed and unconfirmed events).
        /// </summary>
        protected TGrainState State
        {
            get { return this.LogViewAdaptor.TentativeView; }
        }

        /// <summary>
        /// The version of the state.
        /// Always equal to the confirmed version plus the number of unconfirmed events.
        /// </summary>
        protected int Version
        {
            get { return this.LogViewAdaptor.ConfirmedVersion + this.LogViewAdaptor.UnconfirmedSuffix.Count(); }
        }

        /// <summary>
        /// Called whenever the current state may have changed due to local or remote events.
        /// <para>Override this to react to changes of the state.</para>
        /// </summary>
        protected virtual void OnStateChanged()
        {
        }

        /// <summary>
        /// The current confirmed state (includes only confirmed events).
        /// </summary>
        protected TGrainState ConfirmedState
        {
            get { return this.LogViewAdaptor.ConfirmedView; }
        }

        /// <summary>
        /// The version of the confirmed state.
        /// Always equal to the number of confirmed events.
        /// </summary>
        protected int ConfirmedVersion
        {
            get { return this.LogViewAdaptor.ConfirmedVersion; }
        }



        /// <summary>
        /// Called after the confirmed state may have changed (i.e. the confirmed version number is larger).
        /// <para>Override this to react to changes of the confirmed state.</para>
        /// </summary>
        protected virtual void OnConfirmedStateChanged()
        {
            // overridden by journaled grains that want to react to state changes
        }


        /// <summary>
        /// Waits until all previously raised events have been confirmed. 
        /// </summary>
        /// <returns></returns>
        protected Task ConfirmEvents()
        {
            return LogViewAdaptor.ConfirmSubmittedEntriesAsync();

        }

        /// <summary>
        /// Retrieves the latest state now, and confirms all previously raised events. 
        /// Effectively, this enforces synchronization with the global state.
        /// </summary>
        /// <returns></returns>
        protected Task RefreshNow()
        {
            return LogViewAdaptor.SynchronizeNowAsync();
        }


        /// <summary>
        /// Returns the current queue of unconfirmed events.
        /// </summary>
        public IEnumerable<TEventBase> UnconfirmedEvents
        {
            get { return LogViewAdaptor.UnconfirmedSuffix; }
        }


        /// <summary>
        /// Called when the underlying persistence or replication protocol is running into some sort of connection trouble.
        /// <para>Override this to monitor the health of the log-consistency protocol and/or
        /// to customize retry delays.
        /// Any exceptions thrown are caught and logged by the <see cref="ILogConsistencyProvider"/>.</para>
        /// </summary>
        /// <returns>The time to wait before retrying</returns>
        protected virtual void OnConnectionIssue(ConnectionIssue issue)
        {
        }

        /// <summary>
        /// Called when a previously reported connection issue has been resolved.
        /// <para>Override this to monitor the health of the log-consistency protocol. 
        /// Any exceptions thrown are caught and logged by the <see cref="ILogConsistencyProvider"/>.</para>
        /// </summary>
        protected virtual void OnConnectionIssueResolved(ConnectionIssue issue)
        {
        }


        /// <inheritdoc cref="ILogConsistencyDiagnostics.UnresolvedConnectionIssues"/>
        protected IEnumerable<ConnectionIssue> UnresolvedConnectionIssues
        {
            get
            {
                return LogViewAdaptor.UnresolvedConnectionIssues;
            }
        }

        /// <inheritdoc cref="ILogConsistencyDiagnostics.EnableStatsCollection"/>
        protected void EnableStatsCollection()
        {
            LogViewAdaptor.EnableStatsCollection();
        }

        /// <inheritdoc cref="ILogConsistencyDiagnostics.DisableStatsCollection"/>
        protected void DisableStatsCollection()
        {
            LogViewAdaptor.DisableStatsCollection();
        }

        /// <inheritdoc cref="ILogConsistencyDiagnostics.GetStats"/>
        protected LogConsistencyStatistics GetStats()
        {
            return LogViewAdaptor.GetStats();
        }



        /// <summary>
        /// Defines how to apply events to the state. Unless it is overridden in the subclass, it calls
        /// a dynamic "Apply" function on the state, with the event as a parameter.
        /// All exceptions thrown by this method are caught and logged by the log view provider.
        /// <para>Override this to customize how to transition the state for a given event.</para>
        /// </summary>
        /// <param name="state"></param>
        /// <param name="event"></param>
        protected virtual void TransitionState(TGrainState state, TEventBase @event)
        {
            dynamic s = state;
            dynamic e = @event;
            s.Apply(e);
        }



        #region internal plumbing

        /// <summary>
        /// Adaptor for log consistency protocol.
        /// Is installed by the log-consistency provider.
        /// </summary>
        internal ILogViewAdaptor<TGrainState, TEventBase> LogViewAdaptor { get; private set; }

        /// <summary>
        /// Called right after grain is constructed, to install the adaptor.
        /// The log-consistency provider contains a factory method that constructs the adaptor with chosen types for this grain
        /// </summary>
        void ILogConsistentGrain.InstallAdaptor(ILogViewAdaptorFactory factory, object initialState, string graintypename, IStorageProvider storageProvider, IProtocolServices services)
        {
            // call the log consistency provider to construct the adaptor, passing the type argument
            LogViewAdaptor = factory.MakeLogViewAdaptor<TGrainState, TEventBase>(this, (TGrainState)initialState, graintypename, storageProvider, services);
        }

        /// <summary>
        /// If there is no log-consistency provider specified, store versioned state using default storage provider
        /// </summary>
        ILogViewAdaptorFactory ILogConsistentGrain.DefaultAdaptorFactory
        {
            get
            {
                return new StateStorage.DefaultAdaptorFactory();
            }
        }

        /// <summary>
        /// called by adaptor to update the view when entries are appended.
        /// </summary>
        /// <param name="view">log view</param>
        /// <param name="entry">log entry</param>
        void ILogViewAdaptorHost<TGrainState, TEventBase>.UpdateView(TGrainState view, TEventBase entry)
        {
            TransitionState(view, entry);
        }

        /// <summary>
        /// Notify log view adaptor of activation (called before user-level OnActivate)
        /// </summary>
        async Task IProtocolParticipant.PreActivateProtocolParticipant()
        {
            await LogViewAdaptor.PreActivate();
        }

        /// <summary>
        /// Notify log view adaptor of activation (called after user-level OnActivate)
        /// </summary>
        async Task IProtocolParticipant.PostActivateProtocolParticipant()
        {
            await LogViewAdaptor.PostActivate();
        }

        /// <summary>
        /// Notify log view adaptor of deactivation
        /// </summary>
        Task IProtocolParticipant.DeactivateProtocolParticipant()
        {
            return LogViewAdaptor.Deactivate();
        }

        /// <summary>
        /// Receive a protocol message from other clusters, passed on to log view adaptor.
        /// </summary>
        [AlwaysInterleave]
        Task<IProtocolMessage> IProtocolParticipant.OnProtocolMessageReceived(IProtocolMessage payload)
        {
            return LogViewAdaptor.OnProtocolMessageReceived(payload);
        }

        /// <summary>
        /// Receive a configuration change, pass on to log view adaptor.
        /// </summary>
        [AlwaysInterleave]
        Task IProtocolParticipant.OnMultiClusterConfigurationChange(MultiCluster.MultiClusterConfiguration next)
        {
            return LogViewAdaptor.OnMultiClusterConfigurationChange(next);
        }

        /// <summary>
        /// called by adaptor on state change. 
        /// </summary>
        void ILogViewAdaptorHost<TGrainState, TEventBase>.OnViewChanged(bool tentative, bool confirmed)
        {
            if (tentative)
                OnStateChanged();
            if (confirmed)
                OnConfirmedStateChanged();
        }

        /// <summary>
        /// called by adaptor on connection issues. 
        /// </summary>
        void IConnectionIssueListener.OnConnectionIssue(ConnectionIssue connectionIssue)
        {
            OnConnectionIssue(connectionIssue);
        }

        /// <summary>
        /// called by adaptor when a connection issue is resolved. 
        /// </summary>
        void IConnectionIssueListener.OnConnectionIssueResolved(ConnectionIssue connectionIssue)
        {
            OnConnectionIssueResolved(connectionIssue);
        }


        #endregion

    }

}

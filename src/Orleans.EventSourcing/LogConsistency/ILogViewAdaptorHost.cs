namespace Orleans.EventSourcing
{
    /// <summary>
    /// Interface implemented by all grains which use log-view consistency
    /// It gives the log view adaptor access to grain-specific information and callbacks.
    /// </summary>
    /// <typeparam name="TLogView">type of the log view</typeparam>
    /// <typeparam name="TLogEntry">type of log entries</typeparam>
    public interface ILogViewAdaptorHost<TLogView, TLogEntry> : IConnectionIssueListener
    {
        /// <summary>
        /// Implementation of view transitions. 
        /// Any exceptions thrown will be caught and logged as a warning./>.
        /// </summary>
        void UpdateView(TLogView view, TLogEntry entry);

        /// <summary>
        /// Notifies the host grain about state changes. 
        /// Called by <see cref="ILogViewAdaptor{TLogView,TLogEntry}"/> whenever the tentative or confirmed state changes.
        /// Implementations may vary as to whether and how much they batch change notifications.
        /// Any exceptions thrown will be caught and logged as a warning./>.
        /// </summary>
        void OnViewChanged(bool tentative, bool confirmed);

    }


}

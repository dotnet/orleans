using System;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.Common
{
    /// <summary>
    /// Utility class for recording connection issues.
    /// It is public, not internal, because it is a useful building block for implementing other consistency providers.
    /// </summary>
    public struct RecordedConnectionIssue
    {
        /// <summary>
        /// The recorded connection issue, or null if none
        /// </summary>
        public ConnectionIssue Issue { get; private set; }

        /// <summary>
        /// record a connection issue, filling in timestamps etc.
        /// and notify the listener
        /// </summary>
        /// <param name="newIssue">the connection issue to be recorded</param>
        /// <param name="listener">the listener for connection issues</param>
        /// <param name="services">for reporting exceptions in listener</param>
        public void Record(ConnectionIssue newIssue, IConnectionIssueListener listener, ILogConsistencyProtocolServices services)
        {
            newIssue.TimeStamp = DateTime.UtcNow;
            if (Issue != null)
            {
                newIssue.TimeOfFirstFailure = Issue.TimeOfFirstFailure;
                newIssue.NumberOfConsecutiveFailures = Issue.NumberOfConsecutiveFailures + 1;
                newIssue.RetryDelay = newIssue.ComputeRetryDelay(Issue.RetryDelay);
            }
            else
            {
                newIssue.TimeOfFirstFailure = newIssue.TimeStamp;
                newIssue.NumberOfConsecutiveFailures = 1;
                newIssue.RetryDelay = newIssue.ComputeRetryDelay(null);
            }

            Issue = newIssue;

            try
            {
                listener.OnConnectionIssue(newIssue);
            }
            catch (Exception e)
            {
                services.CaughtUserCodeException("OnConnectionIssue", nameof(Record), e);
            }
        }

        /// <summary>
        /// if there is a recorded issue, notify listener and clear it.
        /// </summary>
        /// <param name="listener">the listener for connection issues</param>
        /// <param name="services">for reporting exceptions in listener</param>
        public void Resolve(IConnectionIssueListener listener, ILogConsistencyProtocolServices services)
        {
            if (Issue != null)
            {
                try
                {
                    listener.OnConnectionIssueResolved(Issue);
                }
                catch (Exception e)
                {
                    services.CaughtUserCodeException("OnConnectionIssueResolved", nameof(Record), e);
                }
                Issue = null;
            }
        }

        /// <summary>
        /// delays if there was an issue in last attempt, for the duration specified by the retry delay
        /// </summary>
        /// <returns></returns>
        public async readonly Task DelayBeforeRetry()
        {
            if (Issue == null)
                return;

            await Task.Delay(Issue.RetryDelay);
        }

        /// <inheritdoc/>
        public override readonly string ToString()
        {
            if (Issue == null)
                return "";
            else
                return Issue.ToString();
        }
    }

}

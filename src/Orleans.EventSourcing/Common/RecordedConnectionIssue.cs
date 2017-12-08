using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.LogConsistency;
using System.Threading;

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

        private TaskCompletionSource<bool> tcs;

        /// <summary>
        /// record a connection issue, filling in timestamps etc.
        /// and notify the listener
        /// </summary>
        /// <param name="newIssue">the connection issue to be recorded</param>
        /// <param name="listener">the listener for connection issues</param>
        /// <param name="services">for reporting exceptions in listener</param>
        public void Record(ConnectionIssue newIssue, 
            IConnectionIssueListener listener, 
            ILogConsistencyProtocolServices services)
        {
            newIssue.TimeStamp = DateTime.UtcNow;

            if (Issue != null)
            {
                newIssue.TimeOfFirstFailure = Issue.TimeOfFirstFailure;
                newIssue.NumberOfConsecutiveFailures = Issue.NumberOfConsecutiveFailures + 1;
                newIssue.RetryAfter = Issue.RetryAfter;
            }
            else
            {
                newIssue.TimeOfFirstFailure = newIssue.TimeStamp;
                newIssue.NumberOfConsecutiveFailures = 1;
            }

            // set default delays for next retry
            newIssue.UpdateRetryParameters();

            // record issue
            Issue = newIssue;

            try
            {
                // call user-level monitoring
                // this may update the retry policy
                listener.OnConnectionIssue(newIssue);
            }
            catch (Exception e)
            {
                services.CaughtUserCodeException("OnConnectionIssue", nameof(Record), e);
            }

            // if the retry policy says to resume on activity, create a tcs for tracking that
            if (newIssue.RetryOnActivity)
            {
                tcs = new TaskCompletionSource<bool>();
            }
            else
            {
                tcs = null;
            }
        }


        /// <summary>
        /// Called on application activity
        /// </summary>
        public void Nudge()
        {
            if (tcs != null)
            {
                tcs.TrySetResult(true);
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
                    Issue.TimeStamp = DateTime.UtcNow;
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
        public Task DelayBeforeRetry()
        {
            if (Issue == null)
            {
                return Task.CompletedTask;
            }

            var tasks = new List<Task>();

            if (Issue.RetryAfter != null)
            {
                tasks.Add(Task.Delay(Issue.RetryAfter));
            }
            if (Issue.RetryWhen != null)
            {
                tasks.Add(Issue.RetryWhen);
            }
            if (tcs != null)
            {
                tasks.Add(tcs.Task);
            }

            return Task.WhenAny(tasks);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            if (Issue == null)
                return "";
            else
                return Issue.ToString();
        }
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.LogConsistency;

namespace Orleans.EventSourcing.Common
{

    /// <summary>
    /// Describes a connection issue that occurred when sending update notifications to remote instances.
    /// </summary>
    public class NotificationFailed : ConnectionIssue
    {
        /// <summary> The destination cluster which we could not reach successfully. </summary>
        public string RemoteCluster { get; set; }

        /// <summary> The exception we caught when trying to send the notification message. </summary>
        public Exception Exception { get; set; }

        /// <inheritdoc/>
        public override void UpdateRetryParameters()
        {
            if (NumberOfConsecutiveFailures <= 2)
            {
                RetryAfter = TimeSpan.FromMilliseconds(100);
                RetryOnActivity = false;
            }
            else if (NumberOfConsecutiveFailures <= 20)
            {
                RetryAfter = TimeSpan.FromSeconds(30);
                RetryOnActivity = false;
            }
            else
            {
                // retry in 2-minute-intervals 
                // but freeze after 20 minutes for 6 hours if there is no application activity

                if (NumberOfConsecutiveFailures % 10 != 0)
                {
                    RetryAfter = TimeSpan.FromMinutes(2);
                    RetryOnActivity = false;
                }
                else
                {
                    RetryAfter = TimeSpan.FromHours(6);
                    RetryOnActivity = true;
                }
            }
        }
    }

    /// <summary>
    /// Describes a connection issue that occurred when communicating with primary storage.
    /// </summary>
    public class PrimaryOperationFailed : ConnectionIssue
    {
        /// <summary>
        /// The exception that was caught when communicating with the primary.
        /// </summary>
        public Exception Exception { get; set; }

        /// <inheritdoc/>
        public override void UpdateRetryParameters()
        {
            // after first fail do not backoff yet... retry right away
            if (NumberOfConsecutiveFailures <= 2)
            {
                RetryAfter = TimeSpan.FromMilliseconds(0);
                RetryOnActivity = false;
            }
            // for the next 20 failures do exponential backoff
            else if (NumberOfConsecutiveFailures <= 20)
            {
                if (random == null)
                    random = new Random();

                var backoff = RetryAfter.TotalMilliseconds;

                // grows exponentially up to slowpoll interval
                if (backoff < slowpollinterval)
                {
                    backoff += random.Next(100);
                    backoff = backoff * 1.8;
                }

                RetryAfter = TimeSpan.FromMilliseconds(backoff);
                RetryOnActivity = false;
            }
            else
            {
                // retry in 2-minute-intervals 
                // but freeze after 20 minutes for 6 hours if there is no application activity

                if (NumberOfConsecutiveFailures % 10 != 0)
                {
                    RetryAfter = TimeSpan.FromMinutes(1 + random.NextDouble());
                    RetryOnActivity = false;
                }
                else
                {
                    RetryAfter = TimeSpan.FromHours(6);
                    RetryOnActivity = true;
                }
            }
        }


        [ThreadStatic]
        static Random random;

        private const int slowpollinterval = 10000;
    }








}

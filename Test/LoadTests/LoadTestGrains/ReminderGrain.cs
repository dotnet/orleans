using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;
using Orleans.Runtime;

namespace LoadTestGrains
{
    public class ReminderGrain : Grain, IRemindable, IReminderGrain
    {
        // the following value must be smaller than the SharedEventCounter's report period.
        private static readonly TimeSpan ReportPeriod = TimeSpan.FromSeconds(3);
        
        private readonly Dictionary<string, Tuple<IGrainReminder, TimeSpan?>> reminders = new Dictionary<string, Tuple<IGrainReminder, TimeSpan?>>();

        private Logger _logger;
        private IDisposable _timer;

        private long _ticks;

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();

            _logger = base.GetLogger("ReminderGrain " + base.RuntimeIdentity);

            Random rng = new Random();
            TimeSpan startDelay = TimeSpan.FromMilliseconds(rng.Next((int) ReportPeriod.TotalMilliseconds));
            _timer = RegisterTimer(OnTimer, null, startDelay, ReportPeriod);
        }

        private Task OnTimer(object __unused)
        {
            if (_ticks != 0)
            {
                SharedMemoryCounters.Add(SharedMemoryCounters.CounterIds.EventsConsumed, _ticks, _logger);
                _ticks = 0;
            }

            return TaskDone.Done;
        }

        public async Task ReceiveReminder(string reminderName, TickStatus status)
        {
            _logger.Verbose("Reminder tick");

            Tuple<IGrainReminder, TimeSpan?> reminderAndDurationTuple = null;
            if (!reminders.TryGetValue(reminderName, out reminderAndDurationTuple)
                || reminderAndDurationTuple.Item2 >= status.CurrentTickTime - status.FirstTickTime)
            {
                await this.UnregisterReminder(reminderName);
            }

            _ticks++;
        }

        /// <summary>
        /// Registers the named reminder.
        /// </summary>
        /// <param name="reminderName">Reminder to activate.</param>=
        /// <param name="period">Frequency period for this reminder</param>
        /// <param name="duration">How long to keep the reminder registered for. Any tick after duration will cause a deregister.</param>
        /// <param name="skipGet">Whether to skip the GetReminder call before calling RegisterOrUpdateReminder</param>
        /// <returns>True if the reminder was registered successfully; false otherwise.</returns>
        public async Task<bool> RegisterReminder(string reminderName, TimeSpan period, TimeSpan duration, bool skipGet)
        {
            if (!skipGet)
            {
                var reminderResponse = await this.SafeGetReminderAsync(reminderName);
                if (reminderResponse.Error)
                {
                    // Getting reminder failed. Try to register it anyways. Worst case, this just restarts the time until next tick
                }
                if (!reminderResponse.Error && reminderResponse.GetReminderExists())
                {
                    // The reminder exists and is already registered
                    return true;
                }
            }

            IGrainReminder reminder;
            try
            {
                // we don't stagger the reminders to improve performance because 343 anticipates that bursty registrations will be the common case for their system.
                reminder = await this.RegisterOrUpdateReminder(reminderName, period, period);
            }
            catch (Exception)
            {
                // Registering the reminder failed
                return false;
            }

            // Registering the reminder succeeded, so add it to the cache and return
            this.reminders.Add(reminderName, new Tuple<IGrainReminder, TimeSpan?>(reminder, duration));
            _logger.Verbose("Reminder registered");
            return true;
        }

        /// <summary>
        /// Deactivates the named reminder.
        /// </summary>
        /// <param name="reminderName">Name of the reminder</param>
        /// <returns>
        /// True if the reminder existed and was deactivated successfully or if the reminder did not
        /// exist; false otherwise.
        /// </returns>
        public async Task<bool> UnregisterReminder(string reminderName)
        {
            var reminderResponse = await this.SafeGetReminderAsync(reminderName);
            if (reminderResponse.Error)
            {
                // Getting reminder failed, so we can't unregister it
                return false;
            }

            if (!reminderResponse.GetReminderExists())
            {
                // The reminder does not exist and is not active
                return true;
            }

            try
            {
                // Try to unregister the reminder
                await this.UnregisterReminder(reminderResponse.GetReminder());
            }
            catch (Exception)
            {
                // Unregistering the reminder failed
                return false;
            }
            
            // Unregistering the reminder succeeded, so remove it from the cache and return
            this.reminders.Remove(reminderName);
            _logger.Verbose("Reminder unregistered");
            return true;
        }

        /// <summary>
        /// Gets a reminder using a local cache.
        /// </summary>
        /// <param name="reminderName">Name of the reminder.</param>
        /// <returns>The reminder response.</returns>
        private async Task<SafeGetReminderResponse> SafeGetReminderAsync(string reminderName)
        {
            Tuple<IGrainReminder, TimeSpan?> reminderAndDurationTuple;
            if (this.reminders.TryGetValue(reminderName, out reminderAndDurationTuple))
            {
                // The reminder exists in the cache, so return it
                return SafeGetReminderResponse.CreateResponse(reminderAndDurationTuple.Item1);
            }

            IGrainReminder reminder = null;
            try
            {
                // The reminder does not exist in the cache, so try to get it from Orleans
                reminder = await this.GetReminder(reminderName);
            }
            catch (Exception exception)
            {
                // Orleans throws an ugly exception if GetReminder() fails
                bool exceptionIsBecauseReminderNotFoundAzure =
                    exception is AggregateException
                    && exception.InnerException != null
                    && exception.InnerException.InnerException != null
                    && exception.InnerException.InnerException.InnerException != null
                    && exception.InnerException.InnerException.InnerException.Message.ToLowerInvariant().Contains("<code>resourcenotfound</code>");

                bool exceptionIsBecauseReminderNotFoundInMemory = 
                    exception is AggregateException
                    && exception.InnerException != null
                    && exception.InnerException is KeyNotFoundException;

                bool exceptionIsBecauseReminderNotFound = exceptionIsBecauseReminderNotFoundAzure
                                                            || exceptionIsBecauseReminderNotFoundInMemory;

                if (!exceptionIsBecauseReminderNotFound)
                {
                    // Getting the reminder failed, so return an error
                    return SafeGetReminderResponse.CreateErrorResponse();
                }
            }

            if (reminder == null)
            {
                // The reminder does not exist, so return empty response
                return SafeGetReminderResponse.CreateEmptyResponse();
            }
            else
            {
                // The reminder exists, so add it to the cache and return the reminder
                this.reminders.Add(reminderName, new Tuple<IGrainReminder, TimeSpan?>(reminder, null));
                return SafeGetReminderResponse.CreateResponse(reminder);
            }
        }

        #region SafeGetReminderResponse

        /// <summary>
        /// Response class for <see cref="StreamingGrainBase.SafeGetReminderAsync"/>.
        /// </summary>
        private class SafeGetReminderResponse
        {
            private readonly IGrainReminder reminder;

            private SafeGetReminderResponse(bool error, IGrainReminder reminder = null)
            {
                this.Error = error;
                this.reminder = reminder;
            }

            /// <summary>
            /// Gets whether an error occurred when getting the reminder. If true, then an error
            /// occurred.
            /// </summary>
            public bool Error { get; private set; }

            /// <summary>
            /// Helper method to create an error response.
            /// </summary>
            /// <returns>A response with error set.</returns>
            public static SafeGetReminderResponse CreateErrorResponse()
            {
                return new SafeGetReminderResponse(true);
            }

            /// <summary>
            /// Helper method to create a response containing a reminder.
            /// </summary>
            /// <param name="reminder">The reminder.</param>
            /// <returns>A response with the reminder set.</returns>
            public static SafeGetReminderResponse CreateResponse(IGrainReminder reminder)
            {
                return new SafeGetReminderResponse(false, reminder);
            }

            /// <summary>
            /// Helper method to create a successful response containing no reminder.
            /// </summary>
            /// <returns>A response with the reminder not set.</returns>
            public static SafeGetReminderResponse CreateEmptyResponse()
            {
                return new SafeGetReminderResponse(false);
            }

            /// <summary>
            /// Gets whether the reminder exists. Undefined if an error occurred.
            /// </summary>
            /// <exception cref="InvalidOperationException">
            /// If <see cref="Error"/> is true.
            /// </exception>
            public bool GetReminderExists()
            {
                if (this.Error)
                {
                    throw new InvalidOperationException("An error occurred, so the reminder is undefined");
                }
                return this.reminder != null;
            }

            /// <summary>
            /// Gets the reminder. Undefined if an error occurred.
            /// </summary>
            /// <exception cref="InvalidOperationException">
            /// If <see cref="Error"/> is true.
            /// </exception>
            public IGrainReminder GetReminder()
            {
                if (this.Error)
                {
                    throw new InvalidOperationException("An error occurred, so the reminder is undefined");
                }
                return this.reminder;
            }
        }

        #endregion
    }
}

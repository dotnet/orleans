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

﻿//#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans
{
    /// <summary>
    /// The abstract base class for all grain classes.
    /// </summary>
    public abstract class Grain : IAddressable
    {
        internal CodeGeneration.GrainState GrainState { get; set; }

        internal IActivationData Data;

        internal GrainReference GrainReference { get { return Data.GrainReference; } }

        /// <summary>
        /// This grain's unique identifier.
        /// </summary>
        internal GrainId Identity
        {
            get { return Data.Identity; }
        }

        /// <summary>
        /// String representation of grain's identity including type and primary key.
        /// </summary>
        public string IdentityString
        {
            get { return Data.IdentityString; }
        }

        /// <summary>
        /// A unique identifier for the current silo.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        public string RuntimeIdentity
        {
            get { return Data.RuntimeIdentity; }
        }

        /// <summary>
        /// Registers a timer to send periodic callbacks to this grain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This timer will not prevent the current grain from being deactivated.
        /// If the grain is deactivated, then the timer will be discarded.
        /// </para>
        /// <para>
        /// Until the Task returned from the asyncCallback is resolved, 
        /// the next timer tick will not be scheduled. 
        /// That is to say, timer callbacks never interleave their turns.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <c>Dispose</c> method 
        /// on the timer handle returned from this call.
        /// </para>
        /// <para>
        /// Any exceptions thrown by or faulted Task's returned from the asyncCallback 
        /// will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        /// <param name="asyncCallback">Callback function to be invoked when timr ticks.</param>
        /// <param name="state">State object that will be passed as argument when calling the asyncCallback.</param>
        /// <param name="dueTime">Due time for first timer tick.</param>
        /// <param name="period">Period of subsequent timer ticks.</param>
        /// <returns>Handle for this Timer.</returns>
        /// <seealso cref="IDisposable"/>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            return Data.RegisterTimer(asyncCallback, state, dueTime, period);
        }

        /// <summary>
        /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain.
        /// The grain must implement the <c>Orleans.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
        /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
        /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
        /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
        /// </summary>
        /// <param name="reminderName">Name of this reminder</param>
        /// <param name="dueTime">Due time for this reminder</param>
        /// <param name="period">Frequence period for this reminder</param>
        /// <returns>Promise for Reminder handle.</returns>
        protected Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            if (!(this is IRemindable))
            {
                throw new InvalidOperationException(string.Format("Grain {0} is not 'IRemindable'. A grain should implement IRemindable to use the persistent reminder service", IdentityString));
            }
            if (period < Constants.MinReminderPeriod)
            {
                string msg = string.Format("Cannot register reminder {0}=>{1} as requested period ({2}) is less than minimum allowed reminder period ({3})", IdentityString, reminderName, period, Constants.MinReminderPeriod);
                throw new ArgumentException(msg);
            }
            return RuntimeClient.Current.RegisterOrUpdateReminder(reminderName, dueTime, period);
        }

        /// <summary>
        /// Unregisters a previously registered reminder.
        /// </summary>
        /// <param name="reminder">Reminder to unregister.</param>
        /// <returns>Completion promise for this operation.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected Task UnregisterReminder(IGrainReminder reminder)
        {
            return RuntimeClient.Current.UnregisterReminder(reminder);
        }

        /// <summary>
        /// Returns a previously registered reminder.
        /// </summary>
        /// <param name="reminderName">Reminder to return</param>
        /// <returns>Promise for Reminder handle.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected Task<IGrainReminder> GetReminder(string reminderName)
        {
            return RuntimeClient.Current.GetReminder(reminderName);
        }

        /// <summary>
        /// Returns a list of all reminders registered by the grain.
        /// </summary>
        /// <returns>Promise for list of Reminders registered for this grain.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected Task<List<IGrainReminder>> GetReminders()
        {
            return RuntimeClient.Current.GetReminders();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected IEnumerable<Streams.IStreamProvider> GetStreamProviders()
        {
            return RuntimeClient.Current.CurrentStreamProviderManager.GetStreamProviders();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected Streams.IStreamProvider GetStreamProvider(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException("name");
            return RuntimeClient.Current.CurrentStreamProviderManager.GetProvider(name) as Streams.IStreamProvider;
        }

        /// <summary>
        /// Deactivate this activation of the grain after the current grain method call is completed.
        /// This call will mark this activation of the current grain to be deactivated and removed at the end of the current method.
        /// The next call to this grain will result in a different activation to be used, which typical means a new activation will be created automatically by the runtime.
        /// </summary>
        protected void DeactivateOnIdle()
        {
            Data.DeactivateOnIdle();
        }

        /// <summary>
        /// Delay Deactivation of this activation at least for the specified time duration.
        /// A positive <c>timeSpan</c> value means “prevent GC of this activation for that time span”.
        /// A negative <c>timeSpan</c> value means “cancel the previous setting of the  DelayDeactivation call and make this activation behave based on the regular Activation Garbage Collection settings”.
        /// DeactivateOnIdle method would undo / override any current “keep alive” setting, 
        /// making this grain immediately available  for deactivation.
        /// </summary>
        protected void DelayDeactivation(TimeSpan timeSpan)
        {
            Data.DelayDeactivation(timeSpan);
        }

        /// <summary>
        /// This method is called at the end of the process of activating a grain.
        /// It is called before any messages have been dispatched to the grain.
        /// For grains with declared persistent state, this method is called after the State property has been populated.
        /// </summary>
        public virtual Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// This method is called at the begining of the process of deactivating a grain.
        /// </summary>
        public virtual Task OnDeactivateAsync()
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// Returns a logger object that this grain's code can use for tracing.
        /// </summary>
        /// <returns>Name of the logger to use.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        protected Logger GetLogger(string loggerName)
        {
            return TraceLogger.GetLogger(loggerName, TraceLogger.LoggerType.Grain);
        }

        /// <summary>
        /// Returns a logger object that this grain's code can use for tracing.
        /// The name of the logger will be derived from the grain class name.
        /// </summary>
        /// <returns>A logger for this grain.</returns>
        protected Logger GetLogger()
        {
            return GetLogger(GetType().Name);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        internal string CaptureRuntimeEnvironment()
        {
            return RuntimeClient.Current.CaptureRuntimeEnvironment();
        }
    }

    /// <summary>
    /// Base class for a Grain with declared persistent state.
    /// </summary>
    /// <typeparam name="TGrainState">The interface of the persistent state object</typeparam>
    public class Grain<TGrainState> : Grain
        where TGrainState : class, IGrainState
    {
        /// <summary>
        /// Strongly typed accessor for the grain state 
        /// </summary>
        protected TGrainState State
        {
            get { return base.GrainState as TGrainState; }
        }
    }
}

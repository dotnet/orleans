using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// The abstract base class for all grain classes.
    /// </summary>
    public abstract class Grain : IAddressable, ILifecycleParticipant<IGrainLifecycle>
    {
        // Do not use this directly because we currently don't provide a way to inject it;
        // any interaction with it will result in non unit-testable code. Any behaviour that can be accessed 
        // from within client code (including subclasses of this class), should be exposed through IGrainRuntime.
        // The better solution is to refactor this interface and make it injectable through the constructor.
        internal IActivationData Data;
        public GrainReference GrainReference { get { return Data.GrainReference; } }

        internal IGrainRuntime Runtime { get; }
        /// <summary>
        /// Gets an object which can be used to access other grains. Null if this grain is not associated with a Runtime, such as when created directly for unit testing.
        /// </summary>
        protected IGrainFactory GrainFactory 
        {
            get { return Runtime?.GrainFactory; }
        }

        /// <summary>
        /// Gets the IServiceProvider managed by the runtime. Null if this grain is not associated with a Runtime, such as when created directly for unit testing.
        /// </summary>
        protected internal IServiceProvider ServiceProvider 
        {
            get { return Data?.ActivationServices ?? Runtime?.ServiceProvider; }
        }

        internal GrainId GrainId => this.Data.GrainId;

        /// <summary>
        /// This constructor should never be invoked. We expose it so that client code (subclasses of Grain) do not have to add a constructor.
        /// Client code should use the GrainFactory property to get a reference to a Grain.
        /// </summary>
        protected Grain()
        {
            var context = RuntimeContext.Current;
            if (context is null)
            {
                return;
            }

            if (!(context is IActivationData data))
            {
                ThrowInvalidContext();
                return;
            }

            this.Data = data;
            this.Runtime = data.GrainRuntime;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidContext()
        {
            throw new InvalidOperationException("Current execution context is not suitable for use with Grain");
        }

        /// <summary>
        /// Grain implementers do NOT have to expose this constructor but can choose to do so.
        /// This constructor is particularly useful for unit testing where test code can create a Grain and replace
        /// the IGrainIdentity and IGrainRuntime with test doubles (mocks/stubs).
        /// </summary>
        protected Grain(IGrainRuntime runtime) : this()
        {
            Runtime = runtime;
        }

        /// <summary>
        /// String representation of grain's SiloIdentity including type and primary key.
        /// </summary>
        public string IdentityString => this.GrainId.ToString();

        /// <summary>
        /// A unique identifier for the current silo.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        public string RuntimeIdentity
        {
            get { return Runtime?.SiloIdentity ?? string.Empty; }
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
        /// <param name="asyncCallback">Callback function to be invoked when timer ticks.</param>
        /// <param name="state">State object that will be passed as argument when calling the asyncCallback.</param>
        /// <param name="dueTime">Due time for first timer tick.</param>
        /// <param name="period">Period of subsequent timer ticks.</param>
        /// <returns>Handle for this Timer.</returns>
        /// <seealso cref="IDisposable"/>
        protected IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            if (asyncCallback == null) 
                throw new ArgumentNullException(nameof(asyncCallback));

            EnsureRuntime();
            return Runtime.TimerRegistry.RegisterTimer(this, asyncCallback, state, dueTime, period);
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
        /// <param name="period">Frequency period for this reminder</param>
        /// <returns>Promise for Reminder handle.</returns>
        protected Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            if (string.IsNullOrWhiteSpace(reminderName))
                throw new ArgumentNullException(nameof(reminderName));
            if (!(this is IRemindable))
                throw new InvalidOperationException($"Grain {IdentityString} is not 'IRemindable'. A grain should implement IRemindable to use the persistent reminder service");

            EnsureRuntime();
            return Runtime.ReminderRegistry.RegisterOrUpdateReminder(reminderName, dueTime, period);
        }

        /// <summary>
        /// Unregisters a previously registered reminder.
        /// </summary>
        /// <param name="reminder">Reminder to unregister.</param>
        /// <returns>Completion promise for this operation.</returns>
        protected Task UnregisterReminder(IGrainReminder reminder)
        {
            if (reminder == null)
                throw new ArgumentNullException(nameof(reminder));

            EnsureRuntime();
            return Runtime.ReminderRegistry.UnregisterReminder(reminder);
        }

        /// <summary>
        /// Returns a previously registered reminder.
        /// </summary>
        /// <param name="reminderName">Reminder to return</param>
        /// <returns>Promise for Reminder handle.</returns>
        protected Task<IGrainReminder> GetReminder(string reminderName)
        {
            if (string.IsNullOrWhiteSpace(reminderName))
                throw new ArgumentNullException(nameof(reminderName));

            EnsureRuntime();
            return Runtime.ReminderRegistry.GetReminder(reminderName);
        }

        /// <summary>
        /// Returns a list of all reminders registered by the grain.
        /// </summary>
        /// <returns>Promise for list of Reminders registered for this grain.</returns>
        protected Task<List<IGrainReminder>> GetReminders()
        {
            EnsureRuntime();
            return Runtime.ReminderRegistry.GetReminders();
        }

        /// <summary>
        /// Deactivate this activation of the grain after the current grain method call is completed.
        /// This call will mark this activation of the current grain to be deactivated and removed at the end of the current method.
        /// The next call to this grain will result in a different activation to be used, which typical means a new activation will be created automatically by the runtime.
        /// </summary>
        protected void DeactivateOnIdle()
        {
            EnsureRuntime();
            Runtime.DeactivateOnIdle(this);
        }

        /// <summary>
        /// Delay Deactivation of this activation at least for the specified time duration.
        /// A positive <c>timeSpan</c> value means “prevent GC of this activation for that time span”.
        /// A negative <c>timeSpan</c> value means “cancel the previous setting of the DelayDeactivation call and make this activation behave based on the regular Activation Garbage Collection settings”.
        /// DeactivateOnIdle method would undo / override any current “keep alive” setting, 
        /// making this grain immediately available for deactivation.
        /// </summary>
        protected void DelayDeactivation(TimeSpan timeSpan)
        {
            EnsureRuntime();
            Runtime.DelayDeactivation(this, timeSpan);
        }

        /// <summary>
        /// This method is called at the end of the process of activating a grain.
        /// It is called before any messages have been dispatched to the grain.
        /// For grains with declared persistent state, this method is called after the State property has been populated.
        /// </summary>
        public virtual Task OnActivateAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// This method is called at the beginning of the process of deactivating a grain.
        /// </summary>
        public virtual Task OnDeactivateAsync()
        {
            return Task.CompletedTask;
        }

        private void EnsureRuntime()
        {
            if (Runtime == null)
            {
                throw new InvalidOperationException("Grain was created outside of the Orleans creation process and no runtime was specified.");
            }
        }

        public virtual void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe(this.GetType().FullName, GrainLifecycleStage.Activate, ct => OnActivateAsync(), ct => OnDeactivateAsync());
        }
    }

    /// <summary>
    /// Base class for a Grain with declared persistent state.
    /// </summary>
    /// <typeparam name="TGrainState">The class of the persistent state object</typeparam>
    public class Grain<TGrainState> : Grain
    {
        private IStorage<TGrainState> storage;

        /// <summary>
        /// This constructor should never be invoked. We expose it so that client code (subclasses of this class) do not have to add a constructor.
        /// Client code should use the GrainFactory to get a reference to a Grain.
        /// </summary>
        protected Grain()
        {
        }

        /// <summary>
        /// Grain implementers do NOT have to expose this constructor but can choose to do so.
        /// This constructor is particularly useful for unit testing where test code can create a Grain and replace
        /// the IGrainIdentity, IGrainRuntime and State with test doubles (mocks/stubs).
        /// </summary>
        protected Grain(IStorage<TGrainState> storage)
        {
            this.storage = storage;
        }

        /// <summary>
        /// Strongly typed accessor for the grain state 
        /// </summary>
        protected TGrainState State
        {
            get { return this.storage.State; }
            set { this.storage.State = value; }
        }

        /// <summary>Clear the current grain state data from backing store.</summary>
        protected virtual Task ClearStateAsync()
        {
            return storage.ClearStateAsync();
        }

        /// <summary>Write of the current grain state data into backing store.</summary>
        protected virtual Task WriteStateAsync()
        {
            return storage.WriteStateAsync();
        }

        /// <summary>Read the current grain state data from backing store.</summary>
        /// <remarks>Any previous contents of the grain state data will be overwritten.</remarks>
        protected virtual Task ReadStateAsync()
        {
            return storage.ReadStateAsync();
        }

        public override void Participate(IGrainLifecycle lifecycle)
        {
            base.Participate(lifecycle);
            lifecycle.Subscribe(this.GetType().FullName, GrainLifecycleStage.SetupState, OnSetupState);
        }

        private Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.CompletedTask;
            this.storage = this.Runtime.GetStorage<TGrainState>(this);
            return this.ReadStateAsync();
        }
    }
}

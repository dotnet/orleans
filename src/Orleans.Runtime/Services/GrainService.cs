using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using Orleans.Services;

namespace Orleans.Runtime
{
    /// <summary>Base class for implementing a grain-like partitioned service with per silo instances of it automatically instantiated and started by silo runtime</summary>
    public abstract class GrainService : SystemTarget, IRingRangeListener, IGrainService
    {
        private readonly IConsistentRingProvider ring;
        private readonly string typeName;
        private GrainServiceStatus status;

        private ILogger Logger;

        /// <summary>Gets the token for signaling cancellation upon stopping of grain service</summary>
        protected CancellationTokenSource StoppedCancellationTokenSource { get; }

        /// <summary>Gets the monotonically increasing serial number of the version of the ring range owned by the grain service instance</summary>
        protected int RangeSerialNumber { get; private set; }

        /// <summary>Gets the range of the partitioning ring currently owned by the grain service instance</summary>
        protected IRingRange RingRange { get; private set; }

        /// <summary>Gets the status of the grain service instance</summary>
        protected GrainServiceStatus Status
        {
            get { return status; }
            set
            {
                OnStatusChange(status, value);
                status = value;
            }
        }

        /// <summary>Only to make Reflection happy. Do not use it in your implementation</summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Diagnostics.DebuggerHidden]
        protected GrainService() : base()
        {
            throw new Exception("This should not be constructed by client code.");
        }

        /// <summary>Constructor to use for grain services</summary>
        protected GrainService(GrainId grainId, Silo silo, ILoggerFactory loggerFactory)
            : base(SystemTargetGrainId.Create(grainId.Type, silo.SiloAddress), silo.SiloAddress, lowPriority: true, loggerFactory:loggerFactory)
        {
            typeName = this.GetType().FullName;
            Logger = loggerFactory.CreateLogger(typeName);

            ring = silo.RingProvider;
            StoppedCancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Invoked upon initialization of the service
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public virtual Task Init(IServiceProvider serviceProvider)
        {
            return Task.CompletedTask;
        }

        private void OnStatusChange(GrainServiceStatus oldStatus, GrainServiceStatus newStatus)
        {
            if (oldStatus != GrainServiceStatus.Started && newStatus == GrainServiceStatus.Started)
            {
                ring.SubscribeToRangeChangeEvents(this);
            }
            if (oldStatus != GrainServiceStatus.Stopped && newStatus == GrainServiceStatus.Stopped)
            {
                ring.UnSubscribeFromRangeChangeEvents(this);
            }
        }

        /// <summary>Invoked when service is being started</summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public virtual Task Start()
        {
            RingRange = ring.GetMyRange();
            Logger.Info(ErrorCode.RS_ServiceStarting, "Starting {0} grain service on: {1} x{2,8:X8}, with range {3}", this.typeName, Silo, Silo.GetConsistentHashCode(), RingRange);
            StartInBackground().Ignore();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deferred part of initialization that executes after the service is already started (to speed up startup).
        /// Sets Status to Started.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        protected virtual Task StartInBackground()
        {
            Status = GrainServiceStatus.Started;
            return Task.CompletedTask;
        }

        /// <summary>Invoked when service is being stopped</summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public virtual Task Stop()
        {
            StoppedCancellationTokenSource.Cancel();

            Logger.Info(ErrorCode.RS_ServiceStopping, $"Stopping {this.typeName} grain service");
            Status = GrainServiceStatus.Stopped;

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        void IRingRangeListener.RangeChangeNotification(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            this.WorkItemGroup.QueueTask(() => OnRangeChange(oldRange, newRange, increased), this).Ignore();
        }

        /// <summary>
        /// Invoked when the ring range owned by the service instance changes because of a change in the cluster state
        /// </summary>
        /// <param name="oldRange">The old range.</param>
        /// <param name="newRange">The new range.</param>
        /// <param name="increased">A value indicating whether the range has increased.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public virtual Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            Logger.Info(ErrorCode.RS_RangeChanged, "My range changed from {0} to {1} increased = {2}", oldRange, newRange, increased);
            RingRange = newRange;
            RangeSerialNumber++;

            return Task.CompletedTask;
        }

        /// <summary>Possible statuses of a grain service</summary>
        protected enum GrainServiceStatus
        {
            /// <summary>Initialization is in progress</summary>
            Booting = 0,
            /// <summary>Service successfully started</summary>
            Started,
            /// <summary>Service has been stopped</summary>
            Stopped,
        }
    }
}

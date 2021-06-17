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
        private readonly OrleansTaskScheduler scheduler;
        private readonly IConsistentRingProvider ring;
        private readonly string typeName;
        private GrainServiceStatus status;
        
        private ILogger Logger;
        /// <summary>Token for signaling cancellation upon stopping of grain service</summary>
        protected CancellationTokenSource StoppedCancellationTokenSource { get; }
        /// <summary>Monotonically increasing serial number of the version of the ring range owned by the grain service instance</summary>
        protected int RangeSerialNumber { get; private set; }
        /// <summary>Range of the partitioning ring currently owned by the grain service instance</summary>
        protected IRingRange RingRange { get; private set; }
        /// <summary>Status of the grain service instance</summary>
        protected GrainServiceStatus Status
        {
            get { return status; }
            set
            {
                OnStatusChange(status, value);
                status = value;
            }
        }

        public GrainReference GetGrainReference() => this.GrainReference;

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

            scheduler = silo.LocalScheduler;
            ring = silo.RingProvider;
            StoppedCancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>Invoked upon initialization of the service</summary>
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
        protected virtual Task StartInBackground()
        {
            Status = GrainServiceStatus.Started;
            return Task.CompletedTask;
        }

        /// <summary>Invoked when service is being stopped</summary>
        public virtual Task Stop()
        {
            StoppedCancellationTokenSource.Cancel();

            Logger.Info(ErrorCode.RS_ServiceStopping, $"Stopping {this.typeName} grain service");
            Status = GrainServiceStatus.Stopped;
            
            return Task.CompletedTask;
        }


        void IRingRangeListener.RangeChangeNotification(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            scheduler.QueueTask(() => OnRangeChange(oldRange, newRange, increased), this).Ignore();
        }

        /// <summary>Invoked when the ring range owned by the service instance changes because of a change in the cluster state</summary>
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

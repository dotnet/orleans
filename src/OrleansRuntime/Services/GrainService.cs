using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using Orleans.Services;

namespace Orleans.Runtime
{
    public abstract class GrainService : SystemTarget, IRingRangeListener, IGrainService
    {
        private readonly OrleansTaskScheduler scheduler;
        private readonly IConsistentRingProvider ring;
        private string typeName;
        private GrainServiceStatus status;


        protected Logger Logger { get; }
        protected CancellationTokenSource StoppedCancellationTokenSource { get; }
        protected int RangeSerialNumber { get; private set; }
        protected IRingRange RingRange { get; private set; }
        protected GrainServiceStatus Status
        {
            get { return status; }
            set
            {
                OnStatusChange(status, value);
                status = value;
            }
        }

        public GrainService() : base(null, null)
        {
            throw new Exception("This should not be constructed by client code.");
        }

        protected GrainService(object id, Silo silo) : base((GrainId)id, silo.SiloAddress, lowPriority: true)
        {
            typeName = this.GetType().FullName;
            Logger = LogManager.GetLogger(typeName);

            scheduler = silo.LocalScheduler;
            ring = silo.RingProvider;
            StoppedCancellationTokenSource = new CancellationTokenSource();
        }

        public virtual void Init(IServiceProvider serviceProvider)
        {
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

        public virtual Task Init()
        {
            // Set up here.
            return TaskDone.Done;
        }

        public virtual Task Start()
        {
            Logger.Info(ErrorCode.RS_ServiceStarting, "Starting {0} grain service on: {1} x{2,8:X8}, with range {3}", this.typeName, Silo, Silo.GetConsistentHashCode(), RingRange);
            RingRange = ring.GetMyRange();

            StartInBackground().Ignore();

            return TaskDone.Done;
        }

        protected abstract Task StartInBackground();

        public virtual Task Stop()
        {
            StoppedCancellationTokenSource.Cancel();

            Logger.Info(ErrorCode.RS_ServiceStopping, $"Stopping {this.typeName} grain service");
            Status = GrainServiceStatus.Stopped;
            
            return TaskDone.Done;
        }


        public void RangeChangeNotification(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            scheduler.QueueTask(() => OnRangeChange(oldRange, newRange, increased), this.SchedulingContext).Ignore();
        }

        public virtual Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            Logger.Info(ErrorCode.RS_RangeChanged, "My range changed from {0} to {1} increased = {2}", oldRange, newRange, increased);
            RingRange = newRange;
            RangeSerialNumber++;

            return TaskDone.Done;
        }

        protected enum GrainServiceStatus
        {
            Booting = 0,
            Started,
            Stopped,
        }
    }
}
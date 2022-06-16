using System.Globalization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents the name of a statistic.
    /// </summary>
    public class StatisticName
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StatisticName"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        public StatisticName(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatisticName"/> class.
        /// </summary>
        /// <param name="nameFormat">The name format.</param>
        /// <param name="args">The arguments.</param>
        public StatisticName(StatisticNameFormat nameFormat, params object[] args)
        {
            Name = string.Format(CultureInfo.InvariantCulture, nameFormat.Name, args);
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public string Name { get; }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Represents a format string for a <see cref="StatisticName"/>.
    /// </summary>
    public class StatisticNameFormat
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StatisticNameFormat"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        public StatisticNameFormat(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public string Name { get; }
    }

    internal class StatisticNames
    {
        // Networking
        public const string NETWORKING_SOCKETS_CLOSED = "Networking.Sockets.Closed";
        public const string NETWORKING_SOCKETS_OPENED = "Networking.Sockets.Opened";

        // Messaging
        public const string MESSAGING_SENT_MESSAGES_SIZE = "Messaging.Sent.Messages.Size";
        public const string MESSAGING_RECEIVED_MESSAGES_SIZE = "Messaging.Received.Messages.Size";
        public const string MESSAGING_SENT_BYTES_HEADER = "Messaging.Sent.Bytes.Header";
        public const string MESSAGING_SENT_FAILED = "Messaging.Sent.Failed";
        public const string MESSAGING_SENT_DROPPED = "Messaging.Sent.Dropped";
        public const string MESSAGING_RECEIVED_BYTES_HEADER = "Messaging.Received.Bytes.Header";

        public const string MESSAGING_DISPATCHER_RECEIVED = "Messaging.Processing.Dispatcher.Received";
        public const string MESSAGING_DISPATCHER_PROCESSED = "Messaging.Processing.Dispatcher.Processed";
        public const string MESSAGING_IMA_RECEIVED = "Messaging.Processing.IMA.Received";
        public const string MESSAGING_IMA_ENQUEUED = "Messaging.Processing.IMA.Enqueued";
        public const string MESSAGING_DISPATCHER_FORWARDED = "Messaging.Processing.Dispatcher.Forwarded";
        public const string MESSAGING_PROCESSING_ACTIVATION_DATA_ALL = "Messaging.Processing.ActivationData.All";
        public const string MESSAGING_PINGS_SENT = "Messaging.Pings.Sent";
        public const string MESSAGING_PINGS_RECEIVED = "Messaging.Pings.Received";
        public const string MESSAGING_PINGS_REPLYRECEIVED = "Messaging.Pings.ReplyReceived";
        public const string MESSAGING_PINGS_REPLYMISSED = "Messaging.Pings.ReplyMissed";
        public const string MESSAGING_EXPIRED = "Messaging.Expired";
        public const string MESSAGING_REJECTED = "Messaging.Rejected";
        public const string MESSAGING_REROUTED = "Messaging.Rerouted";
        public const string MESSAGING_SENT_LOCALMESSAGES = "Messaging.Sent.LocalMessages";

        // Queues
        public static readonly StatisticNameFormat QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE = new StatisticNameFormat("Queues.QueueSize.Average.{0}");
        public static readonly StatisticNameFormat QUEUES_ENQUEUED_PER_QUEUE = new StatisticNameFormat("Queues.EnQueued.{0}");
        public static readonly StatisticNameFormat QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE = new StatisticNameFormat("Queues.AverageArrivalRate.RequestsPerSecond.{0}");
        public static readonly StatisticNameFormat QUEUES_TIME_IN_QUEUE_AVERAGE_MILLIS_PER_QUEUE = new StatisticNameFormat("Queues.TimeInQueue.Average.Milliseconds.{0}");
        public static readonly StatisticNameFormat QUEUES_TIME_IN_QUEUE_TOTAL_MILLIS_PER_QUEUE = new StatisticNameFormat("Queues.TimeInQueue.Total.Milliseconds.{0}");


        // Thread tracking
        public static readonly StatisticNameFormat THREADS_PROCESSED_REQUESTS_PER_THREAD = new StatisticNameFormat("Thread.NumProcessedRequests.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_TOTAL_CPU_CYCLES = new StatisticNameFormat("Thread.ExecutionTime.Total.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_TOTAL_WALL_CLOCK = new StatisticNameFormat("Thread.ExecutionTime.Total.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_TOTAL_CPU_CYCLES = new StatisticNameFormat("Thread.ProcessingTime.Total.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_TOTAL_WALL_CLOCK = new StatisticNameFormat("Thread.ProcessingTime.Total.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_AVERAGE_CPU_CYCLES = new StatisticNameFormat("Thread.ExecutionTime.Average.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_AVERAGE_WALL_CLOCK = new StatisticNameFormat("Thread.ExecutionTime.Average.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_AVERAGE_CPU_CYCLES = new StatisticNameFormat("Thread.ProcessingTime.Average.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_AVERAGE_WALL_CLOCK = new StatisticNameFormat("Thread.ProcessingTime.Average.WallClock.Milliseconds.{0}");

        // Stage analysis
        public static readonly StatisticName STAGE_ANALYSIS = new StatisticName("Thread.StageAnalysis");

        // Gateway
        public const string GATEWAY_CONNECTED_CLIENTS = "Gateway.ConnectedClients";
        public const string GATEWAY_SENT = "Gateway.Sent";
        public const string GATEWAY_RECEIVED = "Gateway.Received";
        public const string GATEWAY_LOAD_SHEDDING = "Gateway.LoadShedding";

        // Runtime
        public static readonly StatisticName RUNTIME_CPUUSAGE = new StatisticName("Runtime.CpuUsage");
        public static readonly StatisticName RUNTIME_GC_TOTALMEMORYKB = new StatisticName("Runtime.GC.TotalMemoryKb");
        public static readonly StatisticName RUNTIME_MEMORY_TOTALPHYSICALMEMORYMB = new StatisticName("Runtime.Memory.TotalPhysicalMemoryMb");
        public static readonly StatisticName RUNTIME_MEMORY_AVAILABLEMEMORYMB = new StatisticName("Runtime.Memory.AvailableMemoryMb");
        public static readonly StatisticName RUNTIME_DOT_NET_THREADPOOL_INUSE_WORKERTHREADS = new StatisticName("Runtime.DOT.NET.ThreadPool.InUse.WorkerThreads");
        public static readonly StatisticName RUNTIME_DOT_NET_THREADPOOL_INUSE_COMPLETIONPORTTHREADS = new StatisticName("Runtime.DOT.NET.ThreadPool.InUse.CompletionPortThreads");
        public static readonly StatisticName RUNTIME_IS_OVERLOADED = new StatisticName("Runtime.IsOverloaded");

        public static readonly StatisticNameFormat SCHEDULER_ACTIVATION_TURNSEXECUTED_PERACTIVATION = new StatisticNameFormat("Scheduler.Activation.TurnsExecuted.ByActivation.{0}");
        public static readonly StatisticNameFormat SCHEDULER_ACTIVATION_STATUS_PERACTIVATION = new StatisticNameFormat("Scheduler.Activation.Status.ByActivation.{0}");
        public static readonly StatisticName SCHEDULER_WORKITEMGROUP_COUNT = new StatisticName("Scheduler.WorkItemGroupCount");
        public const string SCHEDULER_NUM_LONG_RUNNING_TURNS = "Scheduler.NumLongRunningTurns";
        public static readonly StatisticName SCHEDULER_NUM_LONG_QUEUE_WAIT_TIMES = new StatisticName("Scheduler.NumLongQueueWaitTimes");

        // Serialization
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_BUFFERS_INPOOL = new StatisticName("Serialization.BufferPool.BuffersInPool");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_ALLOCATED_BUFFERS = new StatisticName("Serialization.BufferPool.AllocatedBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_CHECKED_OUT_BUFFERS = new StatisticName("Serialization.BufferPool.CheckedOutBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_CHECKED_IN_BUFFERS = new StatisticName("Serialization.BufferPool.CheckedInBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_DROPPED_BUFFERS = new StatisticName("Serialization.BufferPool.DroppedBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_DROPPED_TOO_LARGE_BUFFERS = new StatisticName("Serialization.BufferPool.DroppedTooLargeBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_INUSE_CHECKED_OUT_NOT_CHECKED_IN_BUFFERS = new StatisticName("Serialization.BufferPool.InUse.CheckedOutAndNotCheckedIn_Buffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_INUSE_ALLOCATED_NOT_INPOOL_BUFFERS = new StatisticName("Serialization.BufferPool.InUse.AllocatedAndNotInPool_Buffers");
        public static readonly StatisticName SERIALIZATION_BODY_DEEPCOPIES = new StatisticName("Serialization.Body.DeepCopies");
        public static readonly StatisticName SERIALIZATION_BODY_SERIALIZATION = new StatisticName("Serialization.Body.Serializations");
        public static readonly StatisticName SERIALIZATION_BODY_DESERIALIZATION = new StatisticName("Serialization.Body.Deserializations");
        public static readonly StatisticName SERIALIZATION_HEADER_SERIALIZATION = new StatisticName("Serialization.Header.Serializations");
        public static readonly StatisticName SERIALIZATION_HEADER_DESERIALIZATION = new StatisticName("Serialization.Header.Deserializations");
        public static readonly StatisticName SERIALIZATION_HEADER_SERIALIZATION_NUMHEADERS = new StatisticName("Serialization.Header.Serialization.NumHeaders");
        public static readonly StatisticName SERIALIZATION_HEADER_DESERIALIZATION_NUMHEADERS = new StatisticName("Serialization.Header.Deserialization.NumHeaders");
        public static readonly StatisticName SERIALIZATION_BODY_DEEPCOPY_MILLIS = new StatisticName("Serialization.Body.DeepCopy.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_SERIALIZATION_MILLIS = new StatisticName("Serialization.Body.Serialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_DESERIALIZATION_MILLIS = new StatisticName("Serialization.Body.Deserialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_HEADER_SERIALIZATION_MILLIS = new StatisticName("Serialization.Header.Serialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_HEADER_DESERIALIZATION_MILLIS = new StatisticName("Serialization.Header.Deserialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_TOTAL_TIME_IN_SERIALIZER_MILLIS = new StatisticName("Serialization.TotalTimeInSerializer.Milliseconds");

        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_SERIALIZATION = new StatisticName("Serialization.Body.Fallback.Serializations");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DESERIALIZATION = new StatisticName("Serialization.Body.Fallback.Deserializations");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DEEPCOPIES = new StatisticName("Serialization.Body.Fallback.DeepCopies");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_SERIALIZATION_MILLIS = new StatisticName("Serialization.Body.Fallback.Serialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DESERIALIZATION_MILLIS = new StatisticName("Serialization.Body.Fallback.Deserialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DEEPCOPY_MILLIS = new StatisticName("Serialization.Body.Fallback.DeepCopy.Milliseconds");

        // Catalog
        public const string CATALOG_ACTIVATION_COUNT = "Catalog.Activation.CurrentCount";
        public const string CATALOG_ACTIVATION_CREATED = "Catalog.Activation.Created";
        public const string CATALOG_ACTIVATION_DESTROYED = "Catalog.Activation.Destroyed";
        public const string CATALOG_ACTIVATION_FAILED_TO_ACTIVATE = "Catalog.Activation.FailedToActivate";
        public const string CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS = "Catalog.Activation.Collection.NumberOfCollections";
        public const string CATALOG_ACTIVATION_SHUTDOWN = "Catalog.Activation.Shutdown";
        public const string CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS = "Catalog.Activation.NonExistentActivations";
        public const string CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS = "Catalog.Activation.ConcurrentRegistrationAttempts";

        // Dispatcher
        public static readonly StatisticName DISPATCHER_NEW_PLACEMENT = new StatisticName("Dispatcher.NewPlacement");

        // Directory
        public const string DIRECTORY_LOOKUPS_LOCAL_ISSUED = "Directory.Lookups.Local.Issued";
        public const string DIRECTORY_LOOKUPS_LOCAL_SUCCESSES = "Directory.Lookups.Local.Successes";
        public const string DIRECTORY_LOOKUPS_FULL_ISSUED = "Directory.Lookups.Full.Issued";
        public const string DIRECTORY_LOOKUPS_REMOTE_SENT = "Directory.Lookups.Remote.Sent";
        public const string DIRECTORY_LOOKUPS_REMOTE_RECEIVED = "Directory.Lookups.Remote.Received";
        public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED = "Directory.Lookups.LocalDirectory.Issued";
        public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES = "Directory.Lookups.LocalDirectory.Successes";
        public const string DIRECTORY_LOOKUPS_CACHE_ISSUED = "Directory.Lookups.Cache.Issued";
        public const string DIRECTORY_LOOKUPS_CACHE_SUCCESSES = "Directory.Lookups.Cache.Successes";
        // TODO: provide query for this
        public const string DIRECTORY_LOOKUPS_CACHE_HITRATIO = "Directory.Lookups.Cache.HitRatio";
        public const string DIRECTORY_VALIDATIONS_CACHE_SENT = "Directory.Validations.Cache.Sent";
        public const string DIRECTORY_VALIDATIONS_CACHE_RECEIVED = "Directory.Validations.Cache.Received";
        public const string DIRECTORY_PARTITION_SIZE = "Directory.PartitionSize";
        public const string DIRECTORY_CACHE_SIZE = "Directory.CacheSize";
        public static readonly StatisticName DIRECTORY_RING = new StatisticName("Directory.Ring");
        public const string DIRECTORY_RING_RINGSIZE = "Directory.Ring.RingSize";
        public const string DIRECTORY_RING_MYPORTION_RINGDISTANCE = "Directory.Ring.MyPortion.RingDistance";
        public const string DIRECTORY_RING_MYPORTION_RINGPERCENTAGE = "Directory.Ring.MyPortion.RingPercentage";
        public const string DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE = "Directory.Ring.MyPortion.AverageRingPercentage";
        public static readonly StatisticName DIRECTORY_RING_PREDECESSORS = new StatisticName("Directory.Ring.MyPredecessors");
        public static readonly StatisticName DIRECTORY_RING_SUCCESSORS = new StatisticName("Directory.Ring.MySuccessors");

        public static readonly StatisticName DIRECTORY_REGISTRATIONS_ISSUED = new StatisticName("Directory.Registrations.Issued");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_LOCAL = new StatisticName("Directory.Registrations.Local");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_REMOTE_SENT = new StatisticName("Directory.Registrations.Remote.Sent");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_REMOTE_RECEIVED = new StatisticName("Directory.Registrations.Remote.Received");
        public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED = "Directory.Registrations.SingleAct.Issued";
        public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL = "Directory.Registrations.SingleAct.Local";
        public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT = "Directory.Registrations.SingleAct.Remote.Sent";
        public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED = "Directory.Registrations.SingleAct.Remote.Received";
        public const string DIRECTORY_UNREGISTRATIONS_ISSUED = "Directory.UnRegistrations.Issued";
        public const string DIRECTORY_UNREGISTRATIONS_LOCAL = "Directory.UnRegistrations.Local";
        public const string DIRECTORY_UNREGISTRATIONS_REMOTE_SENT = "Directory.UnRegistrations.Remote.Sent";
        public const string DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED = "Directory.UnRegistrations.Remote.Received";
        public const string DIRECTORY_UNREGISTRATIONS_MANY_ISSUED = "Directory.UnRegistrationsMany.Issued";
        public const string DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT = "Directory.UnRegistrationsMany.Remote.Sent";
        public const string DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED = "Directory.UnRegistrationsMany.Remote.Received";

        // ConsistentRing
        public static readonly StatisticName CONSISTENTRING_RING = new StatisticName("ConsistentRing.Ring");
        public const string CONSISTENTRING_RINGSIZE = "ConsistentRing.RingSize";
        public static readonly StatisticName CONSISTENTRING_MYRANGE_RINGDISTANCE = new StatisticName("ConsistentRing.MyRange.RingDistance");
        public const string CONSISTENTRING_MYRANGE_RINGPERCENTAGE = "ConsistentRing.MyRange.RingPercentage";
        public const string CONSISTENTRING_AVERAGERINGPERCENTAGE = "ConsistentRing.AverageRangePercentage";

        // Watchdog
        public const string WATCHDOG_NUM_HEALTH_CHECKS = "Watchdog.NumHealthChecks";
        public const string WATCHDOG_NUM_FAILED_HEALTH_CHECKS = "Watchdog.NumFailedHealthChecks";

        // Client
        public const string CLIENT_CONNECTED_GATEWAY_COUNT = "Client.ConnectedGatewayCount";

        // Silo
        public static readonly StatisticName SILO_START_TIME = new StatisticName("Silo.StartTime");

        // Misc
        public const string GRAIN_COUNTS = "Grain.Counts";
        public const string SYSTEM_TARGET_COUNTS = "SystemTarget";

        // App requests
        public const string APP_REQUESTS_LATENCY_HISTOGRAM = "App.Requests.LatencyHistogram.Millis";
        public const string APP_REQUESTS_TIMED_OUT = "App.Requests.TimedOut";

        // Reminders
        public const string REMINDERS_TARDINESS = "Reminders.Tardiness";
        public const string REMINDERS_NUMBER_ACTIVE_REMINDERS = "Reminders.NumberOfActiveReminders";
        public const string REMINDERS_COUNTERS_TICKS_DELIVERED = "Reminders.TicksDelivered";

        // Storage
        public const string STORAGE_READ_ERRORS = "Storage.Read.Errors";
        public const string STORAGE_WRITE_ERRORS = "Storage.Write.Errors";
        public const string STORAGE_CLEAR_ERRORS = "Storage.Clear.Errors";
        public const string STORAGE_READ_LATENCY = "Storage.Read.Latency";
        public const string STORAGE_WRITE_LATENCY = "Storage.Write.Latency";
        public const string STORAGE_CLEAR_LATENCY = "Storage.Clear.Latency";

        // Streams
        public const string STREAMS_PUBSUB_PRODUCERS_ADDED = "Streams.PubSub.Producers.Added";
        public const string STREAMS_PUBSUB_PRODUCERS_REMOVED = "Streams.PubSub.Producers.Removed";
        public const string STREAMS_PUBSUB_PRODUCERS_TOTAL = "Streams.PubSub.Producers.Total";
        public const string STREAMS_PUBSUB_CONSUMERS_ADDED = "Streams.PubSub.Consumers.Added";
        public const string STREAMS_PUBSUB_CONSUMERS_REMOVED = "Streams.PubSub.Consumers.Removed";
        public const string STREAMS_PUBSUB_CONSUMERS_TOTAL = "Streams.PubSub.Consumers.Total";

        public const string STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS = "Streams.PersistentStream.NumPullingAgents";
        public const string STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES = "Streams.PersistentStream.NumReadMessages";
        public const string STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES = "Streams.PersistentStream.NumSentMessages";
        public const string STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE = "Streams.PersistentStream.PubSubCacheSize";
    }
}

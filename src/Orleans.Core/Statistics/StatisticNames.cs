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

        // Dispatcher
        public static readonly StatisticName DISPATCHER_NEW_PLACEMENT = new StatisticName("Dispatcher.NewPlacement");

        // Directory
        public static readonly StatisticName DIRECTORY_RING = new StatisticName("Directory.Ring");
        public static readonly StatisticName DIRECTORY_RING_PREDECESSORS = new StatisticName("Directory.Ring.MyPredecessors");
        public static readonly StatisticName DIRECTORY_RING_SUCCESSORS = new StatisticName("Directory.Ring.MySuccessors");

        public static readonly StatisticName DIRECTORY_REGISTRATIONS_ISSUED = new StatisticName("Directory.Registrations.Issued");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_LOCAL = new StatisticName("Directory.Registrations.Local");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_REMOTE_SENT = new StatisticName("Directory.Registrations.Remote.Sent");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_REMOTE_RECEIVED = new StatisticName("Directory.Registrations.Remote.Received");

        // ConsistentRing
        public static readonly StatisticName CONSISTENTRING_RING = new StatisticName("ConsistentRing.Ring");
        public static readonly StatisticName CONSISTENTRING_MYRANGE_RINGDISTANCE = new StatisticName("ConsistentRing.MyRange.RingDistance");

        // Silo
        public static readonly StatisticName SILO_START_TIME = new StatisticName("Silo.StartTime");

    }
}

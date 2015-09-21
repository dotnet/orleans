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

using System;

namespace Orleans.Runtime
{
    internal class StatisticName
    {
        public string Name { get; private set; }

        public StatisticName(string name)
        {
            Name = name;
        }

        public StatisticName(StatisticNameFormat nameFormat, params object[] args)
        {
            Name = String.Format(nameFormat.Name, args);
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal class StatisticNameFormat : StatisticName
    {
        public StatisticNameFormat(string name) : base(name)
        {
        }
    }

    internal class StatisticNames
    {
        // Networking
        public static readonly StatisticName NETWORKING_SOCKETS_SILO_SENDING_CLOSED         = new StatisticName("Networking.Sockets.Silo.Sending.Closed");
        public static readonly StatisticName NETWORKING_SOCKETS_SILO_SENDING_OPENED         = new StatisticName("Networking.Sockets.Silo.Sending.Opened");
        public static readonly StatisticName NETWORKING_SOCKETS_SILO_RECEIVING_CLOSED       = new StatisticName("Networking.Sockets.Silo.Receiving.Closed");
        public static readonly StatisticName NETWORKING_SOCKETS_SILO_RECEIVING_OPENED       = new StatisticName("Networking.Sockets.Silo.Receiving.Opened");
        public static readonly StatisticName NETWORKING_SOCKETS_GATEWAYTOCLIENT_DUPLEX_CLOSED    = new StatisticName("Networking.Sockets.GatewayToClient.Duplex.Closed");
        public static readonly StatisticName NETWORKING_SOCKETS_GATEWAYTOCLIENT_DUPLEX_OPENED    = new StatisticName("Networking.Sockets.GatewayToClient.Duplex.Opened");
        public static readonly StatisticName NETWORKING_SOCKETS_CLIENTTOGATEWAY_DUPLEX_CLOSED    = new StatisticName("Networking.Sockets.ClientToGateway.Duplex.Closed");
        public static readonly StatisticName NETWORKING_SOCKETS_CLIENTTOGATEWAY_DUPLEX_OPENED    = new StatisticName("Networking.Sockets.ClientToGateway.Duplex.Opened");

        // Messaging
        public static readonly StatisticName MESSAGING_SENT_MESSAGES_TOTAL                  = new StatisticName("Messaging.Sent.Messages.Total");
        public static readonly StatisticNameFormat MESSAGING_SENT_MESSAGES_PER_DIRECTION   = new StatisticNameFormat("Messaging.Sent.Direction.{0}");
        public static readonly StatisticNameFormat MESSAGING_SENT_MESSAGES_PER_SILO        = new StatisticNameFormat("Messaging.Sent.Messages.To.{0}");
        public static readonly StatisticName MESSAGING_SENT_BYTES_TOTAL                     = new StatisticName("Messaging.Sent.Bytes.Total");
        public static readonly StatisticName MESSAGING_SENT_BYTES_HEADER                    = new StatisticName("Messaging.Sent.Bytes.Header");
        public static readonly StatisticName MESSAGING_SENT_MESSAGESIZEHISTOGRAM            = new StatisticName("Messaging.Sent.MessageSizeHistogram.Bytes");
        public static readonly StatisticNameFormat MESSAGING_SENT_FAILED_PER_DIRECTION     = new StatisticNameFormat("Messaging.Sent.Failed.{0}");
        public static readonly StatisticNameFormat MESSAGING_SENT_DROPPED_PER_DIRECTION    = new StatisticNameFormat("Messaging.Sent.Dropped.{0}");
        public static readonly StatisticNameFormat MESSAGING_SENT_BATCH_SIZE_PER_SOCKET_DIRECTION                  = new StatisticNameFormat("Messaging.Sent.BatchSize.PerSocketDirection.{0}");
        public static readonly StatisticNameFormat MESSAGING_SENT_BATCH_SIZE_BYTES_HISTOGRAM_PER_SOCKET_DIRECTION  = new StatisticNameFormat("Messaging.Sent.BatchSizeBytesHistogram.Bytes.PerSocketDirection.{0}");

        public static readonly StatisticName MESSAGING_RECEIVED_MESSAGES_TOTAL                      = new StatisticName("Messaging.Received.Messages.Total");
        public static readonly StatisticNameFormat MESSAGING_RECEIVED_MESSAGES_PER_DIRECTION       = new StatisticNameFormat("Messaging.Received.Direction.{0}");
        public static readonly StatisticNameFormat MESSAGING_RECEIVED_MESSAGES_PER_SILO            = new StatisticNameFormat("Messaging.Received.Messages.From.{0}");
        public static readonly StatisticName MESSAGING_RECEIVED_BYTES_TOTAL                         = new StatisticName("Messaging.Received.Bytes.Total");
        public static readonly StatisticName MESSAGING_RECEIVED_BYTES_HEADER                        = new StatisticName("Messaging.Received.Bytes.Header");
        public static readonly StatisticName MESSAGING_RECEIVED_MESSAGESIZEHISTOGRAM                = new StatisticName("Messaging.Received.MessageSizeHistogram.Bytes");
        public static readonly StatisticNameFormat MESSAGING_RECEIVED_BATCH_SIZE_PER_SOCKET_DIRECTION                  = new StatisticNameFormat("Messaging.Received.BatchSize.PerSocketDirection.{0}");
        public static readonly StatisticNameFormat MESSAGING_RECEIVED_BATCH_SIZE_BYTES_HISTOGRAM_PER_SOCKET_DIRECTION  = new StatisticNameFormat("Messaging.Received.BatchSizeBytesHistogram.Bytes.PerSocketDirection.{0}");

        public static readonly StatisticNameFormat MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION     = new StatisticNameFormat("Messaging.Processing.Dispatcher.Received.Direction.{0}");
        public static readonly StatisticName MESSAGING_DISPATCHER_RECEIVED_TOTAL                    = new StatisticName("Messaging.Processing.Dispatcher.Received.Total");
        public static readonly StatisticName MESSAGING_DISPATCHER_RECEIVED_ON_NULL                  = new StatisticName("Messaging.Processing.Dispatcher.Received.OnNullContext");
        public static readonly StatisticName MESSAGING_DISPATCHER_RECEIVED_ON_ACTIVATION            = new StatisticName("Messaging.Processing.Dispatcher.Received.OnActivationContext");

        public static readonly StatisticNameFormat MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION         = new StatisticNameFormat("Messaging.Processing.Dispatcher.Processed.Ok.Direction.{0}");
        public static readonly StatisticNameFormat MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION     = new StatisticNameFormat("Messaging.Processing.Dispatcher.Processed.Errors.Direction.{0}");
        public static readonly StatisticNameFormat MESSAGING_DISPATCHER_PROCESSED_REROUTE_PER_DIRECTION    = new StatisticNameFormat("Messaging.Processing.Dispatcher.Processed.ReRoute.Direction.{0}");
        public static readonly StatisticName MESSAGING_DISPATCHER_PROCESSED_TOTAL                           = new StatisticName("Messaging.Processing.Dispatcher.Processed.Total");
       
        public static readonly StatisticName MESSAGING_IMA_RECEIVED                                 = new StatisticName("Messaging.Processing.IMA.Received");
        public static readonly StatisticName MESSAGING_IMA_ENQUEUED_TO_NULL                         = new StatisticName("Messaging.Processing.IMA.Enqueued.ToNullContex");
        public static readonly StatisticName MESSAGING_IMA_ENQUEUED_TO_SYSTEM_TARGET                = new StatisticName("Messaging.Processing.IMA.Enqueued.ToSystemTargetContex");
        public static readonly StatisticName MESSAGING_IMA_ENQUEUED_TO_ACTIVATION                   = new StatisticName("Messaging.Processing.IMA.Enqueued.ToActivationContex");

        public static readonly StatisticName MESSAGING_IGC_FORWARDED                                = new StatisticName("Messaging.Processing.IGC.Forwarded");
        public static readonly StatisticName MESSAGING_IGC_RESENT                                   = new StatisticName("Messaging.Processing.IGC.Resent");
        public static readonly StatisticName MESSAGING_IGC_REROUTE                                  = new StatisticName("Messaging.Processing.IGC.ReRoute");
        public static readonly StatisticName MESSAGING_PROCESSING_ACTIVATION_DATA_ALL               = new StatisticName("Messaging.Processing.ActivationData.All");

        public static readonly StatisticNameFormat MESSAGING_PINGS_SENT_PER_SILO               = new StatisticNameFormat("Messaging.Pings.Sent.{0}");
        public static readonly StatisticNameFormat MESSAGING_PINGS_RECEIVED_PER_SILO           = new StatisticNameFormat("Messaging.Pings.Received.{0}");
        public static readonly StatisticNameFormat MESSAGING_PINGS_REPLYRECEIVED_PER_SILO      = new StatisticNameFormat("Messaging.Pings.ReplyReceived.{0}");
        public static readonly StatisticNameFormat MESSAGING_PINGS_REPLYMISSED_PER_SILO        = new StatisticNameFormat("Messaging.Pings.ReplyMissed.{0}");
        public static readonly StatisticName MESSAGING_EXPIRED_ATSENDER                         = new StatisticName("Messaging.Expired.AtSend");
        public static readonly StatisticName MESSAGING_EXPIRED_ATRECEIVER                       = new StatisticName("Messaging.Expired.AtReceive");
        public static readonly StatisticName MESSAGING_EXPIRED_ATDISPATCH                       = new StatisticName("Messaging.Expired.AtDispatch");
        public static readonly StatisticName MESSAGING_EXPIRED_ATINVOKE                         = new StatisticName("Messaging.Expired.AtInvoke");
        public static readonly StatisticName MESSAGING_EXPIRED_ATRESPOND                        = new StatisticName("Messaging.Expired.AtRespond");
        public static readonly StatisticNameFormat MESSAGING_REJECTED_PER_DIRECTION            = new StatisticNameFormat("Messaging.Rejected.{0}");
        public static readonly StatisticNameFormat MESSAGING_REROUTED_PER_DIRECTION            = new StatisticNameFormat("Messaging.Rerouted.{0}");
        public static readonly StatisticName MESSAGING_SENT_LOCALMESSAGES                       = new StatisticName("Messaging.Sent.LocalMessages");
        public static readonly StatisticName MESSAGING_SENT_BATCH_SIZE                          = new StatisticName("Messaging.Sent.BatchSize");

        // MessageCenter
        public static readonly StatisticName MESSAGE_CENTER_SEND_QUEUE_LENGTH                   = new StatisticName("MessageCenter.SendQueueLength");
        public static readonly StatisticName MESSAGE_CENTER_RECEIVE_QUEUE_LENGTH                = new StatisticName("MessageCenter.ReceiveQueueLength");

        // Queues
        public static readonly StatisticNameFormat QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE          = new StatisticNameFormat("Queues.QueueSize.Average.{0}");
        public static readonly StatisticNameFormat QUEUES_QUEUE_SIZE_INSTANTANEOUS_PER_QUEUE    = new StatisticNameFormat("Queues.QueueSize.Instantaneous.{0}");
        public static readonly StatisticNameFormat QUEUES_ENQUEUED_PER_QUEUE                    = new StatisticNameFormat("Queues.EnQueued.{0}");
        public static readonly StatisticNameFormat QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE        = new StatisticNameFormat("Queues.AverageArrivalRate.RequestsPerSecond.{0}");
        public static readonly StatisticNameFormat QUEUES_TIME_IN_QUEUE_AVERAGE_MILLIS_PER_QUEUE = new StatisticNameFormat("Queues.TimeInQueue.Average.Milliseconds.{0}");
        public static readonly StatisticNameFormat QUEUES_TIME_IN_QUEUE_TOTAL_MILLIS_PER_QUEUE = new StatisticNameFormat("Queues.TimeInQueue.Total.Milliseconds.{0}");


        // Thread tracking
        public static readonly StatisticNameFormat THREADS_PROCESSED_REQUESTS_PER_THREAD       = new StatisticNameFormat("Thread.NumProcessedRequests.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_TOTAL_CPU_CYCLES     = new StatisticNameFormat("Thread.ExecutionTime.Total.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_TOTAL_WALL_CLOCK     = new StatisticNameFormat("Thread.ExecutionTime.Total.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_TOTAL_CPU_CYCLES    = new StatisticNameFormat("Thread.ProcessingTime.Total.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_TOTAL_WALL_CLOCK    = new StatisticNameFormat("Thread.ProcessingTime.Total.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_AVERAGE_CPU_CYCLES    = new StatisticNameFormat("Thread.ExecutionTime.Average.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_EXECUTION_TIME_AVERAGE_WALL_CLOCK    = new StatisticNameFormat("Thread.ExecutionTime.Average.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_AVERAGE_CPU_CYCLES   = new StatisticNameFormat("Thread.ProcessingTime.Average.CPUCycles.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_PROCESSING_TIME_AVERAGE_WALL_CLOCK   = new StatisticNameFormat("Thread.ProcessingTime.Average.WallClock.Milliseconds.{0}");
        public static readonly StatisticNameFormat THREADS_CONTEXT_SWITCHES                    = new StatisticNameFormat("Thread.ContextSwitches.Total.SwitchCount.{0}");

        // Stage analysis
        public static readonly StatisticName STAGE_ANALYSIS                                     = new StatisticName("Thread.StageAnalysis");

        // Gateway
        public static readonly StatisticName GATEWAY_CONNECTED_CLIENTS              = new StatisticName("Gateway.ConnectedClients");
        public static readonly StatisticName GATEWAY_SENT                           = new StatisticName("Gateway.Sent");
        public static readonly StatisticName GATEWAY_RECEIVED                       = new StatisticName("Gateway.Received");
        public static readonly StatisticName GATEWAY_LOAD_SHEDDING                  = new StatisticName("Gateway.LoadShedding");

        // Runtime
        public static readonly StatisticName RUNTIME_CPUUSAGE                                           = new StatisticName("Runtime.CpuUsage");
        public static readonly StatisticName RUNTIME_GC_TOTALMEMORYKB                                   = new StatisticName("Runtime.GC.TotalMemoryKb");
        public static readonly StatisticName RUNTIME_GC_GENCOLLECTIONCOUNT                              = new StatisticName("Runtime.GC.GenCollectonCount");
        public static readonly StatisticName RUNTIME_GC_GENSIZESKB                                      = new StatisticName("Runtime.GC.GenSizesKb");
        public static readonly StatisticName RUNTIME_GC_PERCENTOFTIMEINGC                               = new StatisticName("Runtime.GC.PercentOfTimeInGC");
        public static readonly StatisticName RUNTIME_GC_ALLOCATEDBYTESINKBPERSEC                        = new StatisticName("Runtime.GC.AllocatedBytesInKbPerSec");
        public static readonly StatisticName RUNTIME_GC_PROMOTEDMEMORYFROMGEN1KB                        = new StatisticName("Runtime.GC.PromotedMemoryFromGen1Kb");
        public static readonly StatisticName RUNTIME_GC_LARGEOBJECTHEAPSIZEKB                           = new StatisticName("Runtime.GC.LargeObjectHeapSizeKb");
        public static readonly StatisticName RUNTIME_GC_PROMOTEDMEMORYFROMGEN0KB                        = new StatisticName("Runtime.GC.PromotedFinalizationMemoryFromGen0Kb");
        public static readonly StatisticName RUNTIME_GC_NUMBEROFINDUCEDGCS                              = new StatisticName("Runtime.GC.NumberOfInducedGCs");
        public static readonly StatisticName RUNTIME_MEMORY_TOTALPHYSICALMEMORYMB                       = new StatisticName("Runtime.Memory.TotalPhysicalMemoryMb");
        public static readonly StatisticName RUNTIME_MEMORY_AVAILABLEMEMORYMB                           = new StatisticName("Runtime.Memory.AvailableMemoryMb");
        public static readonly StatisticName RUNTIME_DOT_NET_THREADPOOL_INUSE_WORKERTHREADS             = new StatisticName("Runtime.DOT.NET.ThreadPool.InUse.WorkerThreads");
        public static readonly StatisticName RUNTIME_DOT_NET_THREADPOOL_INUSE_COMPLETIONPORTTHREADS     = new StatisticName("Runtime.DOT.NET.ThreadPool.InUse.CompletionPortThreads");
        public static readonly StatisticName RUNTIME_IS_OVERLOADED                                      = new StatisticName("Runtime.IsOverloaded");
        public static readonly StatisticNameFormat RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE            = new StatisticNameFormat("Runtime.Threads.AsynchAgent.{0}");
        public static readonly StatisticName RUNTIME_THREADS_ASYNC_AGENT_TOTAL_THREADS_CREATED          = new StatisticNameFormat("Runtime.Threads.AsynchAgent.TotalThreadsCreated");

        // Scheduler
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_APPLICATION_BYALLWORKERTHREADS     = new StatisticName("Scheduler.TurnsExecuted.Application.ByAllWorkerThreads");
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_APPLICATION_BYALLWORKITEMGROUPS    = new StatisticName("Scheduler.TurnsExecuted.Application.ByAllWorkItemGroups");
        public static readonly StatisticNameFormat SCHEDULER_TURNSEXECUTED_APPLICATION_PERTHREAD       = new StatisticNameFormat("Scheduler.TurnsExecuted.Application.ByThread.{0}");
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_SYSTEM_BYALLWORKERTHREADS          = new StatisticName("Scheduler.TurnsExecuted.System.ByAllWorkerThreads");
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_SYSTEM_BYALLWORKITEMGROUPS         = new StatisticName("Scheduler.TurnsExecuted.System.ByAllWorkItemGroups");
        public static readonly StatisticNameFormat SCHEDULER_TURNSEXECUTED_SYSTEM_PERTHREAD            = new StatisticNameFormat("Scheduler.TurnsExecuted.System.ByThread.{0}");
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_NULL_BYALLWORKERTHREADS            = new StatisticName("Scheduler.TurnsExecuted.Null.ByAllWorkerThreads");
        public static readonly StatisticNameFormat SCHEDULER_TURNSEXECUTED_NULL_PERTHREAD              = new StatisticNameFormat("Scheduler.TurnsExecuted.Null.ByThread.{0}");
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_TOTAL_START                        = new StatisticName("Scheduler.TurnsExecuted.Total.Start");
        public static readonly StatisticName SCHEDULER_TURNSEXECUTED_TOTAL_END                          = new StatisticName("Scheduler.TurnsExecuted.Total.End");
       
        public static readonly StatisticNameFormat SCHEDULER_ACTIVATION_TURNSEXECUTED_PERACTIVATION    = new StatisticNameFormat("Scheduler.Activation.TurnsExecuted.ByActivation.{0}");
        public static readonly StatisticNameFormat SCHEDULER_ACTIVATION_STATUS_PERACTIVATION           = new StatisticNameFormat("Scheduler.Activation.Status.ByActivation.{0}");
        public static readonly StatisticName SCHEDULER_TURN_LENGTH_HISTOGRAM                            = new StatisticName("Scheduler.TurnLengthHistogram.Ticks");
        public static readonly StatisticName SCHEDULER_PENDINGWORKITEMS                                 = new StatisticName("Scheduler.PendingWorkItems");
        public static readonly StatisticName SCHEDULER_WORKITEMGROUP_COUNT                              = new StatisticName("Scheduler.WorkItemGroupCount");
        public static readonly StatisticName SCHEDULER_NUM_LONG_RUNNING_TURNS                           = new StatisticName("Scheduler.NumLongRunningTurns");
        public static readonly StatisticName SCHEDULER_NUM_LONG_QUEUE_WAIT_TIMES                        = new StatisticName("Scheduler.NumLongQueueWaitTimes");
        //public static readonly StatisticName SCHEDULER_RUN_QUEUE_LENGTH_LEVEL_ONE                       = new StatisticName("Scheduler.RunQueueLength.LevelOne");
        //public static readonly StatisticName SCHEDULER_RUN_QUEUE_LENGTH_LEVEL_TWO                       = new StatisticName("Scheduler.RunQueueLength.LevelTwo");
        //public static readonly StatisticName SCHEDULER_RUN_QUEUE_LENGTH_TOTAL                           = new StatisticName("Scheduler.RunQueueLength.Total");

        public static readonly StatisticName SCHEDULER_ITEMS_ENQUEUED_TOTAL                             = new StatisticName("Scheduler.Items.EnQueued");
        public static readonly StatisticName SCHEDULER_ITEMS_DEQUEUED_TOTAL                             = new StatisticName("Scheduler.Items.DeQueued");
        public static readonly StatisticName SCHEDULER_ITEMS_DROPPED_TOTAL                              = new StatisticName("Scheduler.Items.Dropped");
        public static readonly StatisticName SCHEDULER_CLOSURE_WORK_ITEMS_CREATED                       = new StatisticName("Scheduler.ClosureWorkItems.Created");
        public static readonly StatisticName SCHEDULER_CLOSURE_WORK_ITEMS_EXECUTED                      = new StatisticName("Scheduler.ClosureWorkItems.Executed");

        // Serialization
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_BUFFERS_INPOOL                            = new StatisticName("Serialization.BufferPool.BuffersInPool");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_ALLOCATED_BUFFERS                         = new StatisticName("Serialization.BufferPool.AllocatedBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_CHECKED_OUT_BUFFERS                       = new StatisticName("Serialization.BufferPool.CheckedOutBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_CHECKED_IN_BUFFERS                        = new StatisticName("Serialization.BufferPool.CheckedInBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_DROPPED_BUFFERS                           = new StatisticName("Serialization.BufferPool.DroppedBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_DROPPED_TOO_LARGE_BUFFERS                 = new StatisticName("Serialization.BufferPool.DroppedTooLargeBuffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_INUSE_CHECKED_OUT_NOT_CHECKED_IN_BUFFERS  = new StatisticName("Serialization.BufferPool.InUse.CheckedOutAndNotCheckedIn_Buffers");
        public static readonly StatisticName SERIALIZATION_BUFFERPOOL_INUSE_ALLOCATED_NOT_INPOOL_BUFFERS        = new StatisticName("Serialization.BufferPool.InUse.AllocatedAndNotInPool_Buffers");
        public static readonly StatisticName SERIALIZATION_BODY_DEEPCOPIES                      = new StatisticName("Serialization.Body.DeepCopies");
        public static readonly StatisticName SERIALIZATION_BODY_SERIALIZATION                   = new StatisticName("Serialization.Body.Serializations");
        public static readonly StatisticName SERIALIZATION_BODY_DESERIALIZATION                 = new StatisticName("Serialization.Body.Deserializations");
        public static readonly StatisticName SERIALIZATION_HEADER_SERIALIZATION                 = new StatisticName("Serialization.Header.Serializations");
        public static readonly StatisticName SERIALIZATION_HEADER_DESERIALIZATION               = new StatisticName("Serialization.Header.Deserializations");
        public static readonly StatisticName SERIALIZATION_HEADER_SERIALIZATION_NUMHEADERS      = new StatisticName("Serialization.Header.Serialization.NumHeaders");
        public static readonly StatisticName SERIALIZATION_HEADER_DESERIALIZATION_NUMHEADERS    = new StatisticName("Serialization.Header.Deserialization.NumHeaders");
        public static readonly StatisticName SERIALIZATION_BODY_DEEPCOPY_MILLIS                 = new StatisticName("Serialization.Body.DeepCopy.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_SERIALIZATION_MILLIS            = new StatisticName("Serialization.Body.Serialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_DESERIALIZATION_MILLIS          = new StatisticName("Serialization.Body.Deserialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_HEADER_SERIALIZATION_MILLIS          = new StatisticName("Serialization.Header.Serialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_HEADER_DESERIALIZATION_MILLIS        = new StatisticName("Serialization.Header.Deserialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_TOTAL_TIME_IN_SERIALIZER_MILLIS      = new StatisticName("Serialization.TotalTimeInSerializer.Milliseconds");

        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_SERIALIZATION          = new StatisticName("Serialization.Body.Fallback.Serializations");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DESERIALIZATION        = new StatisticName("Serialization.Body.Fallback.Deserializations");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DEEPCOPIES             = new StatisticName("Serialization.Body.Fallback.DeepCopies");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_SERIALIZATION_MILLIS   = new StatisticName("Serialization.Body.Fallback.Serialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DESERIALIZATION_MILLIS = new StatisticName("Serialization.Body.Fallback.Deserialization.Milliseconds");
        public static readonly StatisticName SERIALIZATION_BODY_FALLBACK_DEEPCOPY_MILLIS        = new StatisticName("Serialization.Body.Fallback.DeepCopy.Milliseconds");

        // Catalog
        public static readonly StatisticName CATALOG_ACTIVATION_COUNT                                               = new StatisticName("Catalog.Activation.CurrentCount");
        public static readonly StatisticName CATALOG_ACTIVATION_CREATED                                             = new StatisticName("Catalog.Activation.Created");
        public static readonly StatisticName CATALOG_ACTIVATION_DESTROYED                                           = new StatisticName("Catalog.Activation.Destroyed");
        public static readonly StatisticName CATALOG_ACTIVATION_FAILED_TO_ACTIVATE                                  = new StatisticName("Catalog.Activation.FailedToActivate");
        public static readonly StatisticName CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS                    = new StatisticName("Catalog.Activation.Collection.NumberOfCollections");
        public static readonly StatisticName CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION                             = new StatisticName("Catalog.Activation.Shutdown.ViaCollection");
        public static readonly StatisticName CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE                     = new StatisticName("Catalog.Activation.Shutdown.ViaDeactivateOnIdle");
        public static readonly StatisticName CATALOG_ACTIVATION_SHUTDOWN_VIA_DIRECT_SHUTDOWN                        = new StatisticName("Catalog.Activation.Shutdown.ViaDirectShutdown");
        public static readonly StatisticName CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS                            = new StatisticName("Catalog.Activation.NonExistentActivations");
        public static readonly StatisticName CATALOG_ACTIVATION_DUPLICATE_ACTIVATIONS                               = new StatisticName("Catalog.Activation.DuplicateActivations");

        // Dispatcher
        public static readonly StatisticName DISPATCHER_NEW_PLACEMENT                                               = new StatisticName("Dispatcher.NewPlacement");

        // Directory
        public static readonly StatisticName DIRECTORY_LOOKUPS_LOCAL_ISSUED                     = new StatisticName("Directory.Lookups.Local.Issued");
        public static readonly StatisticName DIRECTORY_LOOKUPS_LOCAL_SUCCESSES                  = new StatisticName("Directory.Lookups.Local.Successes");
        public static readonly StatisticName DIRECTORY_LOOKUPS_FULL_ISSUED                      = new StatisticName("Directory.Lookups.Full.Issued");
        public static readonly StatisticName DIRECTORY_LOOKUPS_REMOTE_SENT                      = new StatisticName("Directory.Lookups.Remote.Sent");
        public static readonly StatisticName DIRECTORY_LOOKUPS_REMOTE_RECEIVED                  = new StatisticName("Directory.Lookups.Remote.Received");
        public static readonly StatisticName DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED            = new StatisticName("Directory.Lookups.LocalDirectory.Issued");
        public static readonly StatisticName DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES         = new StatisticName("Directory.Lookups.LocalDirectory.Successes");
        public static readonly StatisticName DIRECTORY_LOOKUPS_CACHE_ISSUED                     = new StatisticName("Directory.Lookups.Cache.Issued");
        public static readonly StatisticName DIRECTORY_LOOKUPS_CACHE_SUCCESSES                  = new StatisticName("Directory.Lookups.Cache.Successes");
        public static readonly StatisticName DIRECTORY_LOOKUPS_CACHE_HITRATIO                   = new StatisticName("Directory.Lookups.Cache.HitRatio");
        public static readonly StatisticName DIRECTORY_VALIDATIONS_CACHE_SENT                   = new StatisticName("Directory.Validations.Cache.Sent");
        public static readonly StatisticName DIRECTORY_VALIDATIONS_CACHE_RECEIVED               = new StatisticName("Directory.Validations.Cache.Received");
        public static readonly StatisticName DIRECTORY_PARTITION_SIZE                           = new StatisticName("Directory.PartitionSize");
        public static readonly StatisticName DIRECTORY_CACHE_SIZE                               = new StatisticName("Directory.CacheSize");
        public static readonly StatisticName DIRECTORY_RING                                     = new StatisticName("Directory.Ring");
        public static readonly StatisticName DIRECTORY_RING_RINGSIZE                            = new StatisticName("Directory.Ring.RingSize");
        public static readonly StatisticName DIRECTORY_RING_MYPORTION_RINGDISTANCE              = new StatisticName("Directory.Ring.MyPortion.RingDistance");
        public static readonly StatisticName DIRECTORY_RING_MYPORTION_RINGPERCENTAGE            = new StatisticName("Directory.Ring.MyPortion.RingPercentage");
        public static readonly StatisticName DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE     = new StatisticName("Directory.Ring.MyPortion.AverageRingPercentage");
        public static readonly StatisticName DIRECTORY_RING_PREDECESSORS                        = new StatisticName("Directory.Ring.MyPredecessors");
        public static readonly StatisticName DIRECTORY_RING_SUCCESSORS                          = new StatisticName("Directory.Ring.MySuccessors");

        public static readonly StatisticName DIRECTORY_REGISTRATIONS_ISSUED                     = new StatisticName("Directory.Registrations.Issued");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_LOCAL                      = new StatisticName("Directory.Registrations.Local");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_REMOTE_SENT                = new StatisticName("Directory.Registrations.Remote.Sent");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_REMOTE_RECEIVED            = new StatisticName("Directory.Registrations.Remote.Received");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED          = new StatisticName("Directory.Registrations.SingleAct.Issued");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL           = new StatisticName("Directory.Registrations.SingleAct.Local");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT     = new StatisticName("Directory.Registrations.SingleAct.Remote.Sent");
        public static readonly StatisticName DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED = new StatisticName("Directory.Registrations.SingleAct.Remote.Received");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_ISSUED                   = new StatisticName("Directory.UnRegistrations.Issued");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_LOCAL                    = new StatisticName("Directory.UnRegistrations.Local");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_REMOTE_SENT              = new StatisticName("Directory.UnRegistrations.Remote.Sent");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED          = new StatisticName("Directory.UnRegistrations.Remote.Received");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_MANY_ISSUED              = new StatisticName("Directory.UnRegistrationsMany.Issued");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT         = new StatisticName("Directory.UnRegistrationsMany.Remote.Sent");
        public static readonly StatisticName DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED     = new StatisticName("Directory.UnRegistrationsMany.Remote.Received");

        // ConsistentRing
        public static readonly StatisticName CONSISTENTRING_RING                                = new StatisticName("ConsistentRing.Ring");
        public static readonly StatisticName CONSISTENTRING_RINGSIZE                            = new StatisticName("ConsistentRing.RingSize");
        public static readonly StatisticName CONSISTENTRING_MYRANGE_RINGDISTANCE              = new StatisticName("ConsistentRing.MyRange.RingDistance");
        public static readonly StatisticName CONSISTENTRING_MYRANGE_RINGPERCENTAGE            = new StatisticName("ConsistentRing.MyRange.RingPercentage");
        public static readonly StatisticName CONSISTENTRING_AVERAGERINGPERCENTAGE     = new StatisticName("ConsistentRing.AverageRangePercentage");

        // Membership
        public static readonly StatisticName MEMBERSHIP_ACTIVE_CLUSTER                  = new StatisticName("Membership.ActiveCluster");
        public static readonly StatisticName MEMBERSHIP_ACTIVE_CLUSTER_SIZE             = new StatisticName("Membership.ActiveClusterSize");
        
        // Watchdog
        public static readonly StatisticName WATCHDOG_NUM_HEALTH_CHECKS                 = new StatisticName("Watchdog.NumHealthChecks");
        public static readonly StatisticName WATCHDOG_NUM_FAILED_HEALTH_CHECKS          = new StatisticName("Watchdog.NumFailedHealthChecks");

        // Client
        public static readonly StatisticName CLIENT_CONNECTED_GATEWAY_COUNT                  = new StatisticName("Client.ConnectedGatewayCount");

        // Silo
        public static readonly StatisticName SILO_START_TIME                            = new StatisticName("Silo.StartTime");

        // Misc
        public static readonly StatisticNameFormat GRAIN_COUNTS_PER_GRAIN               = new StatisticNameFormat("Grain.{0}");
        public static readonly StatisticNameFormat SYSTEM_TARGET_COUNTS                 = new StatisticNameFormat("SystemTarget.{0}");

        // App requests
        public static readonly StatisticName APP_REQUESTS_LATENCY_HISTOGRAM             = new StatisticName("App.Requests.LatencyHistogram.Millis");
        public static readonly StatisticName APP_REQUESTS_LATENCY_AVERAGE               = new StatisticName("App.Requests.Latency.Average.Millis");
        public static readonly StatisticName APP_REQUESTS_LATENCY_TOTAL                 = new StatisticName("App.Requests.Latency.Total.Millis");
        public static readonly StatisticName APP_REQUESTS_TIMED_OUT                     = new StatisticName("App.Requests.TimedOut");
        public static readonly StatisticName APP_REQUESTS_TOTAL_NUMBER_OF_REQUESTS      = new StatisticName("App.Requests.Total.Requests");
        public static readonly StatisticName APP_REQUESTS_TPS_LATEST                    = new StatisticName("App.Requests.TPS.Latest");
        public static readonly StatisticName APP_REQUESTS_TPS_TOTAL_SINCE_START         = new StatisticName("App.Requests.TPS.Total.SinceStart");

        // Reminders
        public static readonly StatisticName REMINDERS_AVERAGE_TARDINESS_SECONDS        = new StatisticName("Reminders.AverageTardiness.Seconds");
        public static readonly StatisticName REMINDERS_NUMBER_ACTIVE_REMINDERS          = new StatisticName("Reminders.NumberOfActiveReminders");
        public static readonly StatisticName REMINDERS_COUNTERS_TICKS_DELIVERED         = new StatisticName("Reminders.TicksDelivered");

        // Storage
        public static readonly StatisticName STORAGE_READ_TOTAL = new StatisticName("Storage.Read.Total");
        public static readonly StatisticName STORAGE_WRITE_TOTAL = new StatisticName("Storage.Write.Total");
        public static readonly StatisticName STORAGE_ACTIVATE_TOTAL = new StatisticName("Storage.Activate.Total");
        public static readonly StatisticName STORAGE_READ_ERRORS = new StatisticName("Storage.Read.Errors");
        public static readonly StatisticName STORAGE_WRITE_ERRORS = new StatisticName("Storage.Write.Errors");
        public static readonly StatisticName STORAGE_ACTIVATE_ERRORS = new StatisticName("Storage.Activate.Errors");
        public static readonly StatisticName STORAGE_READ_LATENCY = new StatisticName("Storage.Read.Latency");
        public static readonly StatisticName STORAGE_WRITE_LATENCY = new StatisticName("Storage.Write.Latency");
        public static readonly StatisticName STORAGE_CLEAR_TOTAL = new StatisticName("Storage.Clear.Total");
        public static readonly StatisticName STORAGE_CLEAR_ERRORS = new StatisticName("Storage.Clear.Errors");
        public static readonly StatisticName STORAGE_CLEAR_LATENCY = new StatisticName("Storage.Clear.Latency");

        // Azure
        public static readonly StatisticName AZURE_SERVER_BUSY = new StatisticName("Storage.Azure.Table.ServerBusy");

        // Streams
        public static readonly StatisticName STREAMS_PUBSUB_PRODUCERS_ADDED   = new StatisticName("Streams.PubSub.Producers.Added");
        public static readonly StatisticName STREAMS_PUBSUB_PRODUCERS_REMOVED = new StatisticName("Streams.PubSub.Producers.Removed");
        public static readonly StatisticName STREAMS_PUBSUB_PRODUCERS_TOTAL   = new StatisticName("Streams.PubSub.Producers.Total");
        public static readonly StatisticName STREAMS_PUBSUB_CONSUMERS_ADDED   = new StatisticName("Streams.PubSub.Consumers.Added");
        public static readonly StatisticName STREAMS_PUBSUB_CONSUMERS_REMOVED = new StatisticName("Streams.PubSub.Consumers.Removed");
        public static readonly StatisticName STREAMS_PUBSUB_CONSUMERS_TOTAL   = new StatisticName("Streams.PubSub.Consumers.Total");

        public static readonly StatisticNameFormat STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS = new StatisticNameFormat("Streams.PersistentStream.{0}.NumPullingAgents");
        public static readonly StatisticNameFormat STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES = new StatisticNameFormat("Streams.PersistentStream.{0}.NumReadMessages");
        public static readonly StatisticNameFormat STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES = new StatisticNameFormat("Streams.PersistentStream.{0}.NumSentMessages");
        public static readonly StatisticNameFormat STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE = new StatisticNameFormat("Streams.PersistentStream.{0}.PubSubCacheSize");
        public static readonly StatisticNameFormat STREAMS_PERSISTENT_STREAM_QUEUE_CACHE_SIZE = new StatisticNameFormat("Streams.PersistentStream.{0}.QueueCacheSize");
    }
}
 

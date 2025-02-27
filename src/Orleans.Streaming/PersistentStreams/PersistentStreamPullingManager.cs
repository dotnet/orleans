using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Configuration;
using RunState = Orleans.Configuration.StreamLifecycleOptions.RunState;
using Orleans.Internal;
using System.Threading;
using Orleans.Streams.Filtering;
using Orleans.Runtime.Scheduler;
using System.Diagnostics.Metrics;

namespace Orleans.Streams
{
    internal sealed partial class PersistentStreamPullingManager : SystemTarget, IPersistentStreamPullingManager, IStreamQueueBalanceListener
    {
        private static readonly TimeSpan QUEUES_PRINT_PERIOD = TimeSpan.FromMinutes(5);

        private readonly Dictionary<QueueId, PersistentStreamPullingAgent> queuesToAgentsMap;
        private readonly Dictionary<QueueId, PersistentStreamPullingAgent> deactivatedAgents = new();
        private readonly string streamProviderName;
        private readonly IStreamPubSub pubSub;
        private readonly SystemTargetShared _systemTargetShared;

        private readonly StreamPullingAgentOptions options;
        private readonly AsyncSerialExecutor nonReentrancyGuarantor; // for non-reentrant execution of queue change notifications.
        private readonly ILogger logger;
        private int latestRingNotificationSequenceNumber;
        private int latestCommandNumber;
        private readonly IQueueAdapter queueAdapter;
        private readonly IBackoffProvider _deliveryBackoffProvider;
        private readonly IBackoffProvider _queueReaderBackoffProvider;
        private readonly IQueueAdapterCache queueAdapterCache;
        private IStreamQueueBalancer queueBalancer;
        private readonly IStreamFilter streamFilter;
        private readonly IQueueAdapterFactory adapterFactory;
        private RunState managerState;
        private IDisposable queuePrintTimer;
        private int nextAgentId;
        private int NumberRunningAgents { get { return queuesToAgentsMap.Count; } }


        internal PersistentStreamPullingManager(
            SystemTargetGrainId managerId,
            string strProviderName,
            IStreamPubSub streamPubSub,
            IQueueAdapterFactory adapterFactory,
            IStreamQueueBalancer streamQueueBalancer,
            IStreamFilter streamFilter,
            StreamPullingAgentOptions options,
            IQueueAdapter queueAdapter,
            IBackoffProvider deliveryBackoffProvider,
            IBackoffProvider queueReaderBackoffProvider,
            SystemTargetShared shared)
            : base(managerId, shared)
        {
            if (string.IsNullOrWhiteSpace(strProviderName))
            {
                throw new ArgumentNullException(nameof(strProviderName));
            }

            if (streamPubSub == null)
            {
                throw new ArgumentNullException(nameof(streamPubSub), "StreamPubSub reference should not be null");
            }

            if (streamQueueBalancer == null)
            {
                throw new ArgumentNullException(nameof(streamQueueBalancer), "IStreamQueueBalancer streamQueueBalancer reference should not be null");
            }

            queuesToAgentsMap = new Dictionary<QueueId, PersistentStreamPullingAgent>();
            streamProviderName = strProviderName;
            pubSub = streamPubSub;
            this.options = options;
            nonReentrancyGuarantor = new AsyncSerialExecutor();
            latestRingNotificationSequenceNumber = 0;
            latestCommandNumber = 0;
            queueBalancer = streamQueueBalancer;
            this.streamFilter = streamFilter;
            this.adapterFactory = adapterFactory;
            this.queueAdapter = queueAdapter ?? throw new ArgumentNullException(nameof(queueAdapter));
            _deliveryBackoffProvider = deliveryBackoffProvider;
            _queueReaderBackoffProvider = queueReaderBackoffProvider;
            _systemTargetShared = shared;
            queueAdapterCache = adapterFactory.GetQueueAdapterCache();
            logger = shared.LoggerFactory.CreateLogger($"{GetType().FullName}.{streamProviderName}");
            LogInfoCreated(GetType().Name, streamProviderName);
            StreamInstruments.RegisterPersistentStreamPullingAgentsObserve(() => new Measurement<int>(queuesToAgentsMap.Count, new KeyValuePair<string, object>("name", streamProviderName)));
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public async Task Initialize()
        {
            LogInfoInit();

            await this.queueBalancer.Initialize(this.adapterFactory.GetStreamQueueMapper());
            queueBalancer.SubscribeToQueueDistributionChangeEvents(this);

            List<QueueId> myQueues = queueBalancer.GetMyQueues().ToList();
            LogInfoInitialized(myQueues.Count, new(myQueues));

            queuePrintTimer = this.RegisterTimer(AsyncTimerCallback, null, QUEUES_PRINT_PERIOD, QUEUES_PRINT_PERIOD);
            managerState = RunState.Initialized;
        }

        public async Task Stop()
        {
            await StopAgents();
            if (queuePrintTimer != null)
            {
                queuePrintTimer.Dispose();
                this.queuePrintTimer = null;
            }
            await this.queueBalancer.Shutdown();
            this.queueBalancer = null;
        }

        public async Task StartAgents()
        {
            managerState = RunState.AgentsStarted;
            List<QueueId> myQueues = queueBalancer.GetMyQueues().ToList();

            LogInfoStarting(myQueues.Count, new(myQueues));
            await AddNewQueues(myQueues, true);
            LogInfoStarted();
        }

        public async Task StopAgents()
        {
            managerState = RunState.AgentsStopped;
            List<QueueId> queuesToRemove = queuesToAgentsMap.Keys.ToList();
            LogInfoStopping(queuesToRemove.Count, new(queuesToRemove));
            await RemoveQueues(queuesToRemove);
            LogInfoStopped();
        }

        /// <summary>
        /// Actions to take when the queue distribution changes due to a failure or a join.
        /// Since this pulling manager is system target and queue distribution change notifications
        /// are delivered to it as grain method calls, notifications are not reentrant. To simplify
        /// notification handling we execute them serially, in a non-reentrant way.  We also suppress
        /// and don't execute an older notification if a newer one was already delivered.
        /// </summary>
        public Task QueueDistributionChangeNotification()
        {
            return this.RunOrQueueTask(() => this.HandleQueueDistributionChangeNotification());
        }

        public Task HandleQueueDistributionChangeNotification()
        {
            latestRingNotificationSequenceNumber++;
            int notificationSeqNumber = latestRingNotificationSequenceNumber;
            LogInfoGotQueueChangeNotification(notificationSeqNumber, managerState);
            if (managerState == RunState.AgentsStopped)
            {
                return Task.CompletedTask; // if agents not running, no need to rebalance the queues among them.
            }

            return nonReentrancyGuarantor.AddNext(() =>
            {
                // skip execution of an older/previous notification since already got a newer range update notification.
                if (notificationSeqNumber < latestRingNotificationSequenceNumber)
                {
                    LogInfoSkipQueueChangeNotification(notificationSeqNumber, latestRingNotificationSequenceNumber);
                    return Task.CompletedTask;
                }
                if (managerState == RunState.AgentsStopped)
                {
                    return Task.CompletedTask; // if agents not running, no need to rebalance the queues among them.
                }
                return QueueDistributionChangeNotification(notificationSeqNumber);
            });
        }

        private async Task QueueDistributionChangeNotification(int notificationSeqNumber)
        {
            HashSet<QueueId> currentQueues = queueBalancer.GetMyQueues().ToSet();
            LogInfoExecutingQueueChangeNotification(
                notificationSeqNumber,
                currentQueues.Count,
                new(currentQueues));

            try
            {
                Task t1 = AddNewQueues(currentQueues, false);

                List<QueueId> queuesToRemove = queuesToAgentsMap.Keys.Where(queueId => !currentQueues.Contains(queueId)).ToList();
                Task t2 = RemoveQueues(queuesToRemove);

                await Task.WhenAll(t1, t2);
            }
            finally
            {
                LogInfoDoneExecutingQueueChangeNotification(
                    notificationSeqNumber,
                    NumberRunningAgents,
                    new(queuesToAgentsMap.Keys));
            }
        }

        /// <summary>
        /// Take responsibility for a set of new queues that were assigned to me via a new range.
        /// We first create one pulling agent for every new queue and store them in our internal data structure, then try to initialize the agents.
        /// ERROR HANDLING:
        ///     The responsibility to handle initialization and shutdown failures is inside the Agents code.
        ///     The manager will call Initialize once and log an error. It will not call initialize again and will assume initialization has succeeded.
        ///     Same applies to shutdown.
        /// </summary>
        /// <param name="myQueues"></param>
        /// <param name="failOnInit"></param>
        /// <returns></returns>
        private async Task AddNewQueues(IEnumerable<QueueId> myQueues, bool failOnInit)
        {
            // Create agents for queues in range that we don't yet have.
            // First create them and store in local queuesToAgentsMap.
            // Only after that Initialize them all.
            var agents = new List<PersistentStreamPullingAgent>();
            foreach (var queueId in myQueues)
            {
                if (queuesToAgentsMap.ContainsKey(queueId))
                {
                    continue;
                }
                else if (deactivatedAgents.Remove(queueId, out var agent))
                {
                    queuesToAgentsMap[queueId] = agent;
                    agents.Add(agent);
                }
                else
                {
                    // Create a new agent.
                    try
                    {
                        var agentIdNumber = Interlocked.Increment(ref nextAgentId);
                        var agentId = SystemTargetGrainId.Create(Constants.StreamPullingAgentType, this.Silo, $"{streamProviderName}_{agentIdNumber}_{queueId:H}");
                        IStreamFailureHandler deliveryFailureHandler = await adapterFactory.GetDeliveryFailureHandler(queueId);
                        agent = new PersistentStreamPullingAgent(
                            agentId,
                            streamProviderName,
                            pubSub,
                            streamFilter,
                            queueId,
                            this.options,
                            queueAdapter,
                            queueAdapterCache,
                            deliveryFailureHandler,
                            _deliveryBackoffProvider,
                            _queueReaderBackoffProvider,
                            _systemTargetShared);
                        queuesToAgentsMap.Add(queueId, agent);
                        agents.Add(agent);
                    }
                    catch (Exception exc)
                    {
                        LogErrorCreatingAgent(exc);
                        // What should we do? This error is not recoverable and considered a bug. But we don't want to bring the silo down.
                        // If this is when silo is starting and agent is initializing, fail the silo startup. Otherwise, just swallow to limit impact on other receivers.
                        if (failOnInit) throw;
                    }
                }
            }

            try
            {
                var initTasks = new List<Task>();
                foreach (var agent in agents)
                {
                    initTasks.Add(InitAgent(agent));
                }
                await Task.WhenAll(initTasks);
            }
            catch
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Initialize. No need to log again.
            }
            if (agents.Count > 0)
            {
                LogInfoAddedQueues(
                    agents.Count,
                    new(agents),
                    NumberRunningAgents,
                    new(queuesToAgentsMap.Keys));
            }
        }

        private async Task InitAgent(PersistentStreamPullingAgent agent)
        {
            // Init the agent only after it was registered locally.
            var agentGrainRef = agent.AsReference<IPersistentStreamPullingAgent>();

            // Need to call it as a grain reference.
            try
            {
                await agentGrainRef.Initialize();
            }
            catch (Exception exc)
            {
                LogErrorInitializingAgent(agent.QueueId, exc);
            }
        }

        private async Task RemoveQueues(List<QueueId> queuesToRemove)
        {
            if (queuesToRemove.Count == 0)
            {
                return;
            }
            // Stop the agents that for queues that are not in my range anymore.
            LogInfoRemovingAgents(queuesToRemove.Count, new(queuesToRemove));
            var agents = new List<PersistentStreamPullingAgent>(queuesToRemove.Count);
            var removeTasks = new List<Task>();
            foreach (var queueId in queuesToRemove)
            {
                if (!queuesToAgentsMap.Remove(queueId, out var agent))
                {
                    continue;
                }

                agents.Add(agent);
                deactivatedAgents[queueId] = agent;
                var agentGrainRef = agent.AsReference<IPersistentStreamPullingAgent>();
                var task = OrleansTaskExtentions.SafeExecute(agentGrainRef.Shutdown);
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingManager_11,
                    $"PersistentStreamPullingAgent {agent.QueueId} failed to Shutdown.");
                removeTasks.Add(task);
            }
            try
            {
                await Task.WhenAll(removeTasks);
            }
            catch
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Shutdown. No need to log again.
            }

            if (agents.Count > 0)
            {
                LogInfoRemovedQueues(
                    agents.Count,
                    new(agents),
                    NumberRunningAgents,
                    new(queuesToAgentsMap.Keys));
            }
        }

        public async Task<object> ExecuteCommand(PersistentStreamProviderCommand command, object arg)
        {
            latestCommandNumber++;
            int commandSeqNumber = latestCommandNumber;

            try
            {
                LogInfoGotCommand(
                    command,
                    arg != null ? " with arg " + arg : string.Empty,
                    commandSeqNumber,
                    managerState);

                switch (command)
                {
                    case PersistentStreamProviderCommand.StartAgents:
                    case PersistentStreamProviderCommand.StopAgents:
                        await QueueCommandForExecution(command, commandSeqNumber);
                        return null;
                    case PersistentStreamProviderCommand.GetAgentsState:
                        return managerState;
                    case PersistentStreamProviderCommand.GetNumberRunningAgents:
                        return NumberRunningAgents;
                    default:
                        throw new OrleansException($"PullingAgentManager does not support command {command}.");
                }
            }
            finally
            {
                LogInfoDoneExecutingCommand(
                    command,
                    commandSeqNumber,
                    managerState,
                    NumberRunningAgents);
            }
        }

        // Start and Stop commands are composite commands that take multiple turns.
        // Ee don't wnat them to interleave with other concurrent Start/Stop commands, as well as not with QueueDistributionChangeNotification.
        // Therefore, we serialize them all via the same nonReentrancyGuarantor.
        private Task QueueCommandForExecution(PersistentStreamProviderCommand command, int commandSeqNumber)
        {
            return nonReentrancyGuarantor.AddNext(() =>
            {
                // skip execution of an older/previous command since already got a newer command.
                if (commandSeqNumber < latestCommandNumber)
                {
                    LogInfoSkipCommandExecution(
                        commandSeqNumber,
                        latestCommandNumber);
                    return Task.CompletedTask;
                }
                switch (command)
                {
                    case PersistentStreamProviderCommand.StartAgents:
                        return StartAgents();
                    case PersistentStreamProviderCommand.StopAgents:
                        return StopAgents();
                    default:
                        throw new OrleansException($"PullingAgentManager got unsupported command {command}");
                }
            });
        }

        private static string PrintQueues(ICollection<QueueId> myQueues) => Utils.EnumerableToString(myQueues);

        // Just print our queue assignment periodicaly, for easy monitoring.
        private Task AsyncTimerCallback(object state)
        {
            LogInfoPeriodicPrint(NumberRunningAgents, streamProviderName, new(queuesToAgentsMap.Keys));
            return Task.CompletedTask;
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_01,
            Message = "Created {Name} for Stream Provider {StreamProvider}."
        )]
        private partial void LogInfoCreated(string name, string streamProvider);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_02,
            Message = "Init."
        )]
        private partial void LogInfoInit();

        private readonly struct QueueIdsLogRecord(ICollection<QueueId> queues)
        {
            public override string ToString() => PrintQueues(queues);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_03,
            Message = "Initialize: I am now responsible for {QueueCount} queues: {Queues}."
        )]
        private partial void LogInfoInitialized(int queueCount, QueueIdsLogRecord queues);

        //
        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_Starting,
            Message = "Starting agents for {QueueCount} queues: {Queues}."
        )]
        private partial void LogInfoStarting(int queueCount, QueueIdsLogRecord queues);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_Started,
            Message = "Started agents."
        )]
        private partial void LogInfoStarted();

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_Stopping,
            Message = "Stopping agents for {RemovedCount} queues: {RemovedQueues}."
        )]
        private partial void LogInfoStopping(int removedCount, QueueIdsLogRecord removedQueues);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_Stopped,
            Message = "Stopped agents."
        )]
        private partial void LogInfoStopped();

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_04,
            Message = "Got QueueChangeNotification number {NotificationSequenceNumber} from the queue balancer. managerState = {ManagerState}."
        )]
        private partial void LogInfoGotQueueChangeNotification(int notificationSequenceNumber, RunState managerState);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_05,
            Message = "Skipping execution of QueueChangeNotification number {NotificationSequenceNumber} from the queue allocator since already received a later notification (already have notification number {LatestNotificationNumber})."
        )]
        private partial void LogInfoSkipQueueChangeNotification(int notificationSequenceNumber, int latestNotificationNumber);

        //  "Executing QueueChangeNotification number {NotificationSequenceNumber}. Queue balancer says I should now own {QueueCount} queues: {Queues}"
        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_06,
            Message = "Executing QueueChangeNotification number {NotificationSequenceNumber}. Queue balancer says I should now own {QueueCount} queues: {Queues}."
        )]
        private partial void LogInfoExecutingQueueChangeNotification(int notificationSequenceNumber, int queueCount, QueueIdsLogRecord queues);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_16,
            Message = "Done Executing QueueChangeNotification number {NotificationSequenceNumber}. I now own {QueueCount} queues: {Queues}."
        )]
        private partial void LogInfoDoneExecutingQueueChangeNotification(int notificationSequenceNumber, int queueCount, QueueIdsLogRecord queues);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_07,
            Message = "Exception while creating PersistentStreamPullingAgent."
        )]
        private partial void LogErrorCreatingAgent(Exception exc);

        private readonly struct AgentsLogRecord(List<PersistentStreamPullingAgent> agents)
        {
            public override string ToString() => Utils.EnumerableToString(agents, agent => agent.QueueId.ToString());
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_08,
            Message = "Added {AddedCount} new queues: {AddedQueues}. Now own total of {QueueCount} queues: {Queues}."
        )]
        private partial void LogInfoAddedQueues(int addedCount, AgentsLogRecord addedQueues, int queueCount, QueueIdsLogRecord queues);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_09,
            Message = "PersistentStreamPullingAgent {QueueId} failed to Initialize."
        )]
        private partial void LogErrorInitializingAgent(QueueId queueId, Exception exc);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_10,
            Message = "About to remove {RemovedCount} agents from my responsibility: {RemovedQueues}."
        )]
        private partial void LogInfoRemovingAgents(int removedCount, QueueIdsLogRecord removedQueues);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_10,
            Message = "Removed {RemovedCount} queues: {RemovedQueues}. Now own total of {QueueCount} queues: {Queues}."
        )]
        private partial void LogInfoRemovedQueues(int removedCount, AgentsLogRecord removedQueues, int queueCount, QueueIdsLogRecord queues);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_13,
            Message = "Got command {Command}{ArgString}: commandSeqNumber = {CommandSequenceNumber}, managerState = {ManagerState}."
        )]
        private partial void LogInfoGotCommand(PersistentStreamProviderCommand command, string argString, int commandSequenceNumber, RunState managerState);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_15,
            Message = "Done executing command {Command}: commandSeqNumber = {CommandSequenceNumber}, managerState = {ManagerState}, num running agents = {NumRunningAgents}."
        )]
        private partial void LogInfoDoneExecutingCommand(PersistentStreamProviderCommand command, int commandSequenceNumber, RunState managerState, int numRunningAgents);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_15,
            Message = "Skipping execution of command number {CommandNumber} since already received a later command (already have command number {LatestCommandNumber})."
        )]
        private partial void LogInfoSkipCommandExecution(int commandNumber, int latestCommandNumber);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingManager_PeriodicPrint,
            Message = "I am responsible for a total of {QueueCount} queues on stream provider {StreamProviderName}: {Queues}."
        )]
        private partial void LogInfoPeriodicPrint(int queueCount, string streamProviderName, QueueIdsLogRecord queues);
    }
}

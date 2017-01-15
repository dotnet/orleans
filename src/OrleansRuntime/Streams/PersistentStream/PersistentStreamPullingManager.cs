using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class PersistentStreamPullingManager : SystemTarget, IPersistentStreamPullingManager, IStreamQueueBalanceListener
    {
        private static readonly TimeSpan QUEUES_PRINT_PERIOD = TimeSpan.FromMinutes(5);

        private readonly Dictionary<QueueId, PersistentStreamPullingAgent> queuesToAgentsMap;
        private readonly string streamProviderName;
        private readonly IStreamProviderRuntime providerRuntime;
        private readonly IStreamPubSub pubSub;

        private readonly PersistentStreamProviderConfig config;
        private readonly AsyncSerialExecutor nonReentrancyGuarantor; // for non-reentrant execution of queue change notifications.
        private readonly LoggerImpl logger;

        private int latestRingNotificationSequenceNumber;
        private int latestCommandNumber;
        private IQueueAdapter queueAdapter;
        private readonly IQueueAdapterCache queueAdapterCache;
        private readonly IStreamQueueBalancer queueBalancer;
        private readonly IQueueAdapterFactory adapterFactory;
        private PersistentStreamProviderState managerState;
        private readonly IDisposable queuePrintTimer;
        private int NumberRunningAgents { get { return queuesToAgentsMap.Count; } }

        internal PersistentStreamPullingManager(
            GrainId id, 
            string strProviderName, 
            IStreamProviderRuntime runtime,
            IStreamPubSub streamPubSub,
            IQueueAdapterFactory adapterFactory,
            IStreamQueueBalancer streamQueueBalancer,
            PersistentStreamProviderConfig config)
            : base(id, runtime.ExecutingSiloAddress)
        {
            if (string.IsNullOrWhiteSpace(strProviderName))
            {
                throw new ArgumentNullException("strProviderName");
            }
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime", "IStreamProviderRuntime runtime reference should not be null");
            }
            if (streamPubSub == null)
            {
                throw new ArgumentNullException("streamPubSub", "StreamPubSub reference should not be null");
            }
            if (streamQueueBalancer == null)
            {
                throw new ArgumentNullException("streamQueueBalancer", "IStreamQueueBalancer streamQueueBalancer reference should not be null");
            }

            queuesToAgentsMap = new Dictionary<QueueId, PersistentStreamPullingAgent>();
            streamProviderName = strProviderName;
            providerRuntime = runtime;
            pubSub = streamPubSub;
            this.config = config;
            nonReentrancyGuarantor = new AsyncSerialExecutor();
            latestRingNotificationSequenceNumber = 0;
            latestCommandNumber = 0;
            queueBalancer = streamQueueBalancer;
            this.adapterFactory = adapterFactory;

            queueAdapterCache = adapterFactory.GetQueueAdapterCache();
            logger = LogManager.GetLogger(GetType().Name + "-" + streamProviderName, LoggerType.Provider);
            Log(ErrorCode.PersistentStreamPullingManager_01, "Created {0} for Stream Provider {1}.", GetType().Name, streamProviderName);

            IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS, strProviderName), () => queuesToAgentsMap.Count);
            queuePrintTimer = base.RegisterTimer(AsyncTimerCallback, null, QUEUES_PRINT_PERIOD, QUEUES_PRINT_PERIOD);
        }

        public Task Initialize(Immutable<IQueueAdapter> qAdapter)
        {
            if (qAdapter.Value == null) throw new ArgumentNullException("qAdapter", "Init: queueAdapter should not be null");

            Log(ErrorCode.PersistentStreamPullingManager_02, "Init.");

            // Remove cast once we cleanup
            queueAdapter = qAdapter.Value;

            var meAsQueueBalanceListener = this.AsReference<IStreamQueueBalanceListener>();
            queueBalancer.SubscribeToQueueDistributionChangeEvents(meAsQueueBalanceListener);

            List<QueueId> myQueues = queueBalancer.GetMyQueues().ToList();
            Log(ErrorCode.PersistentStreamPullingManager_03, String.Format("Initialize: I am now responsible for {0} queues: {1}.", myQueues.Count, PrintQueues(myQueues)));

            managerState = PersistentStreamProviderState.Initialized;
            return TaskDone.Done;
        }

        public async Task Stop()
        {
            await StopAgents();
            if (queuePrintTimer != null)
            {
                queuePrintTimer.Dispose();
            }
        }

        public async Task StartAgents()
        {
            managerState = PersistentStreamProviderState.AgentsStarted;
            List<QueueId> myQueues = queueBalancer.GetMyQueues().ToList();

            Log(ErrorCode.PersistentStreamPullingManager_Starting, "Starting agents for {0} queues: {1}", myQueues.Count, PrintQueues(myQueues));
            await AddNewQueues(myQueues, true);
            Log(ErrorCode.PersistentStreamPullingManager_Started, "Started agents.");
        }

        public async Task StopAgents()
        {
            managerState = PersistentStreamProviderState.AgentsStopped;
            List<QueueId> queuesToRemove = queuesToAgentsMap.Keys.ToList();
            Log(ErrorCode.PersistentStreamPullingManager_Stopping, "Stopping agents for {0} queues: {1}", queuesToRemove.Count, PrintQueues(queuesToRemove));
            await RemoveQueues(queuesToRemove);
            Log(ErrorCode.PersistentStreamPullingManager_Stopped, "Stopped agents.");
        }

        #region Management of queues

        /// <summary>
        /// Actions to take when the queue distribution changes due to a failure or a join.
        /// Since this pulling manager is system target and queue distribution change notifications
        /// are delivered to it as grain method calls, notifications are not reentrant. To simplify
        /// notification handling we execute them serially, in a non-reentrant way.  We also supress
        /// and don't execute an older notification if a newer one was already delivered.
        /// </summary>
        public Task QueueDistributionChangeNotification()
        {
            latestRingNotificationSequenceNumber++;
            int notificationSeqNumber = latestRingNotificationSequenceNumber;
            Log(ErrorCode.PersistentStreamPullingManager_04,
                "Got QueueChangeNotification number {0} from the queue balancer. managerState = {1}", notificationSeqNumber, managerState);

            if (managerState == PersistentStreamProviderState.AgentsStopped)
            {
                return TaskDone.Done; // if agents not running, no need to rebalance the queues among them.
            }

            return nonReentrancyGuarantor.AddNext(() =>
            {
                // skip execution of an older/previous notification since already got a newer range update notification.
                if (notificationSeqNumber < latestRingNotificationSequenceNumber)
                {
                    Log(ErrorCode.PersistentStreamPullingManager_05,
                        "Skipping execution of QueueChangeNotification number {0} from the queue allocator since already received a later notification " +
                        "(already have notification number {1}).",
                        notificationSeqNumber, latestRingNotificationSequenceNumber);
                    return TaskDone.Done;
                }
                if (managerState == PersistentStreamProviderState.AgentsStopped)
                {
                    return TaskDone.Done; // if agents not running, no need to rebalance the queues among them.
                }
                return QueueDistributionChangeNotification(notificationSeqNumber);
            });
        }

        private async Task QueueDistributionChangeNotification(int notificationSeqNumber)
        {
            HashSet<QueueId> currentQueues = queueBalancer.GetMyQueues().ToSet();
            Log(ErrorCode.PersistentStreamPullingManager_06,
                "Executing QueueChangeNotification number {0}. Queue balancer says I should now own {1} queues: {2}", notificationSeqNumber, currentQueues.Count, PrintQueues(currentQueues));

            try
            {
                Task t1 = AddNewQueues(currentQueues, false);

                List<QueueId> queuesToRemove = queuesToAgentsMap.Keys.Where(queueId => !currentQueues.Contains(queueId)).ToList();
                Task t2 = RemoveQueues(queuesToRemove);

                await Task.WhenAll(t1, t2);
            }
            finally
            {
                Log(ErrorCode.PersistentStreamPullingManager_16,
                    "Done Executing QueueChangeNotification number {0}. I now own {1} queues: {2}", notificationSeqNumber, NumberRunningAgents, PrintQueues(queuesToAgentsMap.Keys));
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
            foreach (var queueId in myQueues.Where(queueId => !queuesToAgentsMap.ContainsKey(queueId)))
            {
                try
                {
                    var agentId = GrainId.NewSystemTargetGrainIdByTypeCode(Constants.PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE);
                    var agent = new PersistentStreamPullingAgent(agentId, streamProviderName, providerRuntime, pubSub, queueId, config);
                    providerRuntime.RegisterSystemTarget(agent);
                    queuesToAgentsMap.Add(queueId, agent);
                    agents.Add(agent);
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.PersistentStreamPullingManager_07, "Exception while creating PersistentStreamPullingAgent.", exc);
                    // What should we do? This error is not recoverable and considered a bug. But we don't want to bring the silo down.
                    // If this is when silo is starting and agent is initializing, fail the silo startup. Otherwise, just swallow to limit impact on other receivers.
                    if (failOnInit) throw;
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
                Log(ErrorCode.PersistentStreamPullingManager_08, "Added {0} new queues: {1}. Now own total of {2} queues: {3}",
                    agents.Count,
                    Utils.EnumerableToString(agents, agent => agent.QueueId.ToString()),
                    NumberRunningAgents,
                    PrintQueues(queuesToAgentsMap.Keys));
            }
        }

        private async Task InitAgent(PersistentStreamPullingAgent agent)
        {
            // Init the agent only after it was registered locally.
            var agentGrainRef = agent.AsReference<IPersistentStreamPullingAgent>();
            var queueAdapterCacheAsImmutable = queueAdapterCache != null ? queueAdapterCache.AsImmutable() : new Immutable<IQueueAdapterCache>(null);
            IStreamFailureHandler deliveryFailureHandler = await adapterFactory.GetDeliveryFailureHandler(agent.QueueId);
            // Need to call it as a grain reference.
            var task = OrleansTaskExtentions.SafeExecute(() => agentGrainRef.Initialize(queueAdapter.AsImmutable(), queueAdapterCacheAsImmutable, deliveryFailureHandler.AsImmutable()));
            await task.LogException(logger, ErrorCode.PersistentStreamPullingManager_09, String.Format("PersistentStreamPullingAgent {0} failed to Initialize.", agent.QueueId));
        }

        private async Task RemoveQueues(List<QueueId> queuesToRemove)
        {
            if (queuesToRemove.Count == 0)
            {
                return;
            }
            // Stop the agents that for queues that are not in my range anymore.
            var agents = new List<PersistentStreamPullingAgent>(queuesToRemove.Count);
            Log(ErrorCode.PersistentStreamPullingManager_10, "About to remove {0} agents from my responsibility: {1}", queuesToRemove.Count, Utils.EnumerableToString(queuesToRemove, q => q.ToString()));
            
            var removeTasks = new List<Task>();
            foreach (var queueId in queuesToRemove)
            {
                PersistentStreamPullingAgent agent;
                if (!queuesToAgentsMap.TryGetValue(queueId, out agent)) continue;

                agents.Add(agent);
                queuesToAgentsMap.Remove(queueId);
                var agentGrainRef = agent.AsReference<IPersistentStreamPullingAgent>();
                var task = OrleansTaskExtentions.SafeExecute(agentGrainRef.Shutdown);
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingManager_11,
                    String.Format("PersistentStreamPullingAgent {0} failed to Shutdown.", agent.QueueId));
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

            foreach (var agent in agents)
            {
                try
                {
                    providerRuntime.UnregisterSystemTarget(agent);
                }
                catch (Exception exc)
                {
                    Log(ErrorCode.PersistentStreamPullingManager_12, 
                        "Exception while UnRegisterSystemTarget of PersistentStreamPullingAgent {0}. Ignoring. Exc.Message = {1}.", ((ISystemTargetBase)agent).GrainId, exc.Message);
                }
            }
            if (agents.Count > 0)
            {
                Log(ErrorCode.PersistentStreamPullingManager_10, "Removed {0} queues: {1}. Now own total of {2} queues: {3}",
                    agents.Count,
                    Utils.EnumerableToString(agents, agent => agent.QueueId.ToString()),
                    NumberRunningAgents,
                    PrintQueues(queuesToAgentsMap.Keys));
            }
        }

        #endregion

        public async Task<object> ExecuteCommand(PersistentStreamProviderCommand command, object arg)
        {
            latestCommandNumber++;
            int commandSeqNumber = latestCommandNumber;

            try
            {
                Log(ErrorCode.PersistentStreamPullingManager_13,
                    String.Format("Got command {0}{1}: commandSeqNumber = {2}, managerState = {3}.",
                    command, arg != null ? " with arg " + arg : String.Empty, commandSeqNumber, managerState));

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
                        throw new OrleansException(String.Format("PullingAgentManager does not support command {0}.", command));
                }
            }
            finally
            {
                Log(ErrorCode.PersistentStreamPullingManager_15,
                    String.Format("Done executing command {0}: commandSeqNumber = {1}, managerState = {2}, num running agents = {3}.", 
                    command, commandSeqNumber, managerState, NumberRunningAgents));
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
                    Log(ErrorCode.PersistentStreamPullingManager_15,
                        "Skipping execution of command number {0} since already received a later command (already have command number {1}).",
                        commandSeqNumber, latestCommandNumber);
                    return TaskDone.Done;
                }
                switch (command)
                {
                    case PersistentStreamProviderCommand.StartAgents:
                        return StartAgents();
                    case PersistentStreamProviderCommand.StopAgents:
                        return StopAgents();
                    default:
                        throw new OrleansException(String.Format("PullingAgentManager got unsupported command {0}", command));
                }
            });
        }

        private static string PrintQueues(ICollection<QueueId> myQueues)
        {
            return Utils.EnumerableToString(myQueues, q => q.ToString());
        }

        // Just print our queue assignment periodicaly, for easy monitoring.
        private Task AsyncTimerCallback(object state)
        {
            Log(ErrorCode.PersistentStreamPullingManager_PeriodicPrint, 
                        "I am responsible for a total of {0} queues on stream provider {1}: {2}.", 
                        NumberRunningAgents, streamProviderName, PrintQueues(queuesToAgentsMap.Keys));
            return TaskDone.Done;
        }

        private void Log(ErrorCode logCode, string format, params object[] args)
        {
            logger.LogWithoutBulkingAndTruncating(Severity.Info, logCode, format, args);
        }
    }
}

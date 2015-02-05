using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Coordination;
using Orleans.Runtime.Transactions;
using Orleans.RuntimeCore;
using Orleans.Scheduler;
using OrleansRuntimeGrainInterfaces;

namespace UnitTests.Transactions
{
    //[TestClass]
    //public class JournalTests
    //{
    //    [TestMethod]
    //    public void JournalExploration()
    //    {
    //        for (var i = 0; i < 100; i++)
    //        {
    //            Explore(3, 100);
    //        }
    //    }

    //    // Entry point for chess testing
    //    public static bool Run()
    //    {
    //        UseChess = true;
    //        bool result;
    //        try
    //        {
    //            new JournalTests().Explore(3, 10);
    //            result = true;
    //        }
    //        catch (Exception e)
    //        {
    //            Console.WriteLine("ChessTest failed: {0}", e);
    //            result = false;
    //        }
    //        return result;
    //    }

    //    class TestSilo
    //    {
    //        public SiloAddress Silo { get; set; }
    //        public Journal Journal { get; set; }
    //        public Catalog Catalog { get; set; }
    //    }

    //    private static bool UseChess;

    //    private TestSilo[] silos;
    //    private Dictionary<SiloAddress, TestSilo> siloIndex;
    //    private List<List<Message>> queues;
    //    private List<RequestInfo> requests;
    //    private List<TaskInfo> tasks;
    //    private List<GrainId> grains;
    //    private List<ActivationAddress> activations;
    //    private Logger logger;
    //    private Random random;
    //    private int counter;

    //    public void Initialize(int siloCount)
    //    {
    //        silos = new TestSilo[siloCount];
    //        siloIndex = new Dictionary<SiloAddress, TestSilo>();
    //        var config = new ClusterConfiguration();
    //        var typeManager = new GrainTypeManager(config.Globals, true);
    //        var scheduler = new OrleansTaskScheduler(config.Globals, config.Defaults);
    //        for (var i = 0; i < silos.Length; i++)
    //        {
    //            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 10000 + i));
    //            Action<Dispatcher> dispatcher;
    //            var catalog = new Catalog(Constants.CatalogId, silo, null,typeManager, scheduler, new TargetDirectory(), config, out dispatcher);
    //            var journal = new Journal(catalog, silo)
    //            {
    //                Validation = Journal.ValidationLevel.ValidateCatalog,
    //                ThrowOnInvalid = UseChess
    //            };
    //            silos[i] = new TestSilo {Catalog = catalog, Journal = journal, Silo = silo};
    //            siloIndex[silo] = silos[i];
    //        }
    //        queues = new List<List<Message>>();
    //        requests = new List<RequestInfo>();
    //        tasks = new List<TaskInfo>();
    //        grains = new List<GrainId>();
    //        activations = new List<ActivationAddress>();
    //        logger = new Logger("JournalTest", Logger.LoggerType.Application);
    //        ((Logger)logger).SetSeverityLevel(Logger.Severity.Verbose);
    //        random = new Random();
    //        counter = 0;
    //    }

    //    public void Explore(int siloCount, int count)
    //    {
    //        var steps = new Action[]
    //        {
    //            NewRequestStep,
    //            NewTaskStep,
    //            NewGrainStep,
    //            NewActivationStep,
    //            SendMessageStep,
    //            ReceiveMessageStep,
    //            CompleteTaskStep,
    //            AbortTaskStep,
    //        };

    //        Initialize(siloCount);

    //        // start at a reasonable initial state
    //        NewGrainStep();
    //        NewActivationStep();
    //        NewRequestStep();
    //        NewTaskStep();
    //        for (int i = 0; i < count; i++)
    //        {
    //            bool ok = false;
    //            const int limit = 10;
    //            int repeats = 0;
    //            while ((!ok) && repeats++ < limit)
    //            {
    //                if (UseChess)
    //                {
    //                    try
    //                    {
    //                        steps[Choose(steps.Length)]();
    //                    }
    //                    catch (DeadEndException)
    //                    {
    //                        i = count; // stop and let chess explore another path
    //                    }
    //                    ok = true;
    //                }
    //                else
    //                {
    //                    try
    //                    {
    //                        switch (Choose(7))
    //                        {
    //                            case 0: case 1: case 2:
    //                                SendMessageStep();
    //                                break;
    //                            case 3: case 4: case 5:
    //                                ReceiveMessageStep();
    //                                break;
    //                            case 6:
    //                                steps[Choose(steps.Length)]();
    //                                break;
    //                        }
    //                    }
    //                    catch (DeadEndException)
    //                    {
    //                        // ignore and retry randomly
    //                    }
    //                    ok = true;
    //                    Validate();
    //                }
    //            }
    //            if (repeats >= limit)
    //                break;
    //        }
    //    }

    //    #region Steps

    //    private void NewRequestStep()
    //    {
    //        var a = ChooseActivation();
    //        var localSiloAddress = SiloAddress.NewLocalAddress(0); // make fake silo address for sender
    //        var message = new Message
    //        {
    //            IsReadOnly = Choose(2) == 1,
    //            Direction = Message.Directions.Request,
    //            TargetAddress = a,
    //            SendingAddress = ActivationAddress.NewActivationAddress(localSiloAddress),
    //            DebugContext = NextString(),
    //        };
    //        var id = RequestId.NewId(message.IsReadOnly);
    //        message.RequestId = id;
    //        var info = new RequestInfo(id, new RequestEntry(id, message));
    //        requests.Add(info);
    //        logger.Verbose("New request {0}", info);
    //    }

    //    private void NewTaskStep()
    //    {
    //        var request = ChooseRequest(t => t.ActiveTask == null);
    //        var journal = GetSilo(request.Entry.Message.TargetSilo).Journal;
    //        var id = journal.NewTask(request.Entry.Message);
    //        TaskInfo info;
    //        journal.TryGetTaskInfo(id, out info);
    //        ReasonDetail reason;
    //        if (! journal.TryJoinTask(id, request.Entry.Message.TargetAddress, out reason))
    //            throw new DeadEndException();
    //        tasks.Add(info);
    //        logger.Verbose("New task {0} for request {1}", id, request);
    //    }

    //    private void CompleteTaskStep()
    //    {
    //        var task = ChooseActiveTask();
    //        TaskInfo info;
    //        var silo = ChooseSilo(s => s.Journal.TryGetTaskInfo(task, out info));
    //        logger.Verbose("Complete task {0} on {1}", task, silo.Silo);
    //        silo.Journal.CompleteTask(task);
    //    }

    //    private void AbortTaskStep()
    //    {
    //        var task = ChooseActiveTask();
    //        TaskInfo info;
    //        var silo = ChooseSilo(s => s.Journal.TryGetTaskInfo(task, out info));
    //        logger.Verbose("Abort task {0} on {1}", task, silo.Silo);
    //        silo.Journal.AbortTask(task, ReasonDetail.Aborted);
    //    }

    //    private void NewGrainStep()
    //    {
    //        grains.Add(GrainId.NewId());
    //        logger.Verbose("New grain {0}", grains[grains.Count - 1]);
    //    }

    //    private void NewActivationStep()
    //    {
    //        var grain = ChooseGrain();
    //        var silo = ChooseSilo();
    //        var address = ActivationAddress.NewActivationAddress(silo.Silo, grain);
    //        silo.Catalog.GetOrCreateActivationData(address);
    //        activations.Add(address);
    //        logger.Verbose("New activation {0}", address);
    //    }

    //    private void SendMessageStep()
    //    {
    //        var task = ChooseActiveTask();
    //        TaskInfo info;
    //        var from = ChooseSilo(s => s.Journal.TryGetTaskInfo(task, out info));
    //        from.Journal.TryGetTaskInfo(task, out info);
    //        var target = ChooseActivation();
    //        var header = from.Journal.GetTaskHeader(task, target.Silo);
    //        var i = queues.FindIndex(l => l[0].SendingSilo.Equals(from.Silo) && l[0].TargetSilo.Equals(target.Silo));
    //        if (i < 0)
    //        {
    //            i = queues.Count;
    //            queues.Add(new List<Message>());
    //        }
    //        var message = new Message
    //        {
    //            SendingSilo = from.Silo,
    //            SendingGrain = GrainId.NewId(),
    //            TargetAddress = target, 
    //            TaskHeader = header
    //        };
    //        queues[i].Add(message);
    //        logger.Verbose("Send message {0}", message);
    //    }

    //    private void ReceiveMessageStep()
    //    {
    //        var i = Choose(queues.Count);
    //        var message = queues[i][0];
    //        queues[i].RemoveAt(0);
    //        if (queues[i].Count == 0)
    //        {
    //            queues.RemoveAt(i);
    //        }

    //        var target = GetSilo(message.TargetSilo);
    //        ReasonDetail detail;
    //        var update = target.Journal.TryAcceptTaskHeader(message.TaskHeader, out detail);
    //        logger.Verbose("Receive request header {0} from {1} to {2} = {3} {4}",
    //            message.TaskHeader, message.SendingSilo, message.TargetAddress, update, detail);

    //        ActivationData activation;
    //        TaskInfo taskInfo;
    //        if (update &&
    //            target.Journal.TryGetTaskInfo(null, out taskInfo) &&
    //            (! taskInfo.Activations.Contains(message.TargetAddress)) &&
    //            target.Catalog.TryGetActivationData(message.TargetActivation, out activation))
    //        {
    //            var joined = target.Journal.TryJoinTask(null, message.TargetAddress, out detail);
    //            logger.Verbose("Join activation {0} to {1} = {2} {3}", message.TargetAddress, null, joined, detail);
    //        }
    //    }

    //    #endregion
    //    #region Exploration support

    //    private void Validate()
    //    {
    //        foreach (var s in silos)
    //        {
    //            s.Journal.Validate();
    //        }
    //    }

    //    private int Choose(int n)
    //    {
    //        if (n <= 0)
    //            throw new DeadEndException();
    //        if (n == 1)
    //            return 0;
    //        if (! UseChess)
    //            return random.Next(n);
    //        return ChessAPI.Choose(n);
    //    }

    //    private TestSilo ChooseSilo()
    //    {
    //        return silos[Choose(silos.Length)];
    //    }

    //    private TestSilo ChooseSilo(Func<TestSilo, bool> predicate)
    //    {
    //        var match = silos.Where(predicate).ToList();
    //        return match[Choose(match.Count)];
    //    }

    //    private TestSilo GetSilo(SiloAddress siloAddress)
    //    {
    //        return siloIndex[siloAddress];
    //    }

    //    private RequestInfo ChooseRequest(Func<RequestInfo, bool> predicate)
    //    {
    //        var list = requests.Where(predicate).ToList();
    //        return list[Choose(list.Count)];
    //    }

    //    private TaskId ChooseActiveTask()
    //    {
    //        TaskInfo info;
    //        var active = tasks.Where(t =>
    //            GetSilo(t.Coordinator).Journal.TryGetTaskInfo(t.Id, out info) &&
    //            info.State == TaskState.Active).ToList();
    //        return active[Choose(active.Count)].Id;
    //    }

    //    private GrainId ChooseGrain()
    //    {
    //        return grains[Choose(grains.Count)];
    //    }

    //    private ActivationAddress ChooseActivation()
    //    {
    //        return activations[Choose(activations.Count)];
    //    }

    //    private ActivationAddress ChooseActivation(Func<ActivationAddress,bool> predicate)
    //    {
    //        var list = activations.Where(predicate).ToList();
    //        return list[Choose(list.Count)];
    //    }

    //    private string NextString()
    //    {
    //        return "s" + counter++;
    //    }

    //    #endregion
    //}

    internal class TestGrainState : GrainState
    {
        public string Label { get; set; }

        public int Version { get; set; }
    }

    internal class DeadEndException : Exception
    {
        public static DeadEndException Instance = new DeadEndException();
    }

    internal class ActivationRecord
    {
        public ActivationState Status { get; set; }

        public ActivationAddress Address { get; set; }

        public GrainState State { get; set; }

        public bool Deleted { get; set; }
    }

    internal class TaskRecord
    {
        public TaskState Status { get; set; }

        public ReasonDetail AbortReason { get; set; }
    }

    internal class RequestRecord
    {

        public RequestState Status { get; set; }

        public TaskId ActiveTask { get; set; }

        public TaskId CommittedTask { get; set; }

        public SiloAddress Silo { get; set; }
    }
}

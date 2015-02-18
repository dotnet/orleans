using System.Collections.Generic;
using Orleans.RuntimeCore;

namespace UnitTests
{
#if ___
    [TestClass]
    public class TaskTest : UnitTestBase
    {
        static readonly Options MyOptions = new Options
        {
            StartFreshOrleans = true,
            Validation = true,
            UseStore = true,
            DisableTasks = false,
            SingleActivationMode = false,
            MaxActiveThreads = 5, //1,
            UseChessMessaging = true,
            CompletionInterval = 0, // no timer
            // UseChessScheduling = true,
            // RandomSeed = -1, // -1 = chess, 0 = random, positive = replay seed
        };

        public TaskTest() : base(MyOptions)
        {
            // todo: start one more silo, so we can explore 3-silo combinations?
        }

        [TestInitialize]
        public void MyTestInitialize()
        {
        }

        [TestCleanup]
        public void MyTestCleanup()
        {
            WaitUntilQuiet();
            Initialize(MyOptions);
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        public TestContext TestContext { set; get; }

        private static int LastContentsHash;

        private static void Validate(params string[] actions)
        {
            if (!MyOptions.UseStore)
                return;
            // get current committed state of all grains, which includes message history
            var contents = primary.GetStorageContents(typeof(TaskTestGrainProperties))
                .Where(pair => pair.Value is TaskTestGrainProperties)
                .ToDictionary(pair => pair.Key, pair => (TaskTestGrainProperties)pair.Value);
            // check if it has changed
            var hash = contents.Aggregate(0, (h, p) => h ^ p.GetHashCode());
            if (LastContentsHash == hash)
                return;
            LastContentsHash = hash;

            // string -> {GrainID} map
            var grains = contents
                .Select(pair => new { Grain = pair.Key, Label = pair.Value.Label })
                .GroupBy(each => each.Label)
                .ToDictionary(group => group.Key, group => group.Select(each => each.Grain).ToSet());

            // set of all initial requests
            var initial = Enumerable.Range(0, actions.Count()/2)
                .Where(i => grains.ContainsKey(actions[i*2]))
                .SelectMany(i => grains[actions[i*2]]
                    .Where(grain => contents[grain].Messages.Any(m => m.Item2 == actions[i*2+1]))
                    .Select(each => each.KeyValue(actions[i*2 + 1])))
                .ToList();
            // actual message flows to every grain
            var actualFlows = TotalFlows(
                contents.SelectMany(pair =>
                    pair.Value.Messages.Select(tuple =>
                        new Flow {Count = 1, Grain = pair.Key, Method = tuple.Item2})));
            // memoize transitive flows for all requests
            var memo = new Dictionary<KeyValuePair<GrainId, string>, TotalFlow>();
            // multiply the flow for each request by the number recorded
            // note: action messages must be distinct - doesn't count them, just checks for existence
            var expectedFlows = TotalFlows(
                initial.Where(request => contents[request.Key].Messages.Any(m => m.Item2 == request.Value))
                .SelectMany(request =>
                    RequestFlows(request, contents, memo)));

            Expect(actualFlows.Keys.SetEquals(expectedFlows.Keys),
                "Only consistent states committed to storage");

            Expect(actualFlows.All(pair => expectedFlows[pair.Key] == pair.Value),
                "Only consistent states committed to storage");
        }

        private static TotalFlow TotalFlows(IEnumerable<Flow> flows)
        {
            return flows
                .GroupBy(flow => flow.Request)
                .Select(g => new Flow { Grain = g.Key.Key, Method = g.Key.Value, Count = g.Sum(g2 => g2.Count) })
                .ToDictionary(flow => flow.Request, flow => flow.Count);
        }

        private static IEnumerable<Flow> RequestFlows(KeyValuePair<GrainId,string> request, Dictionary<GrainId,TaskTestGrainProperties> contents, Dictionary<KeyValuePair<GrainId,string>,TotalFlow> memo)
        {
            TotalFlow result;
            if (!memo.TryGetValue(request, out result))
            {
                var messages = TaskTestGrainUtil.Invocations[request.Value];
                var children = contents[request.Key].Children.Select(c => c.AsReference().GrainId).ToArray();
                var flows = new [] {new Flow {Grain = request.Key, Method = request.Value, Count = 1}}
                    .Concat(Enumerable.Range(0, Math.Min(messages.Length, children.Length))
                        .Where(i => messages[i] != null)
                        .SelectMany(i => RequestFlows(children[i].KeyValue(messages[i]), contents, memo)));
                result = TotalFlows(flows);
            }
            return result.Select(pair => new Flow {Grain = pair.Key.Key, Method = pair.Key.Value, Count = pair.Value});
        }

        internal static void Expect(bool ok, string property = null, Func<string> info = null)
        {
            if (!ok)
            {
                throw new InvalidOperationException((property ?? "Validation failed") + (info != null ? ": " + info() : ""));
            }
        }

        // Entry point for chess testing
        public static bool Run()
        {
            try
            {
                new TaskTest().ChooseOneTest();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("ChessTest failed: {0}", e);
                return false;
            }
        }

        /// <summary>
        /// This chooses one test via chess, so exploration will run through
        /// all permutations of all tests
        /// </summary>
        public void ChooseOneTest()
        {
            switch (ChessAPI.Choose(5))
            {
                case 0:
                    TaskBasics();
                    break;
                case 1:
                    TaskIsolation();
                    break;
                case 2:
                    TaskConsistency();
                    break;
                case 3:
                    TaskAtomicity();
                    break;
                case 4:
                    TaskDurability();
                    break;
            }
        }

        // run all tests outside the test framework, cleaning up after each one
        public void RunAll()
        {
            TaskBasics();
            MyTestCleanup();

            TaskAtomicity();
            MyTestCleanup();

            TaskConsistency();
            MyTestCleanup();

            if (! MyOptions.SingleActivationMode)
            {
                // only run if multiple activations enabled, otherwise it deadlocks
                TaskIsolation();
                MyTestCleanup();
            }
            TaskDurability();
            MyTestCleanup();
        }

        // need a level of indirection to force call-by-reference from silo's app domain
        static Action RemoteValidator(params string[] actions)
        {
            var action = new RemoteAction(() => Validate(actions));
            return action.Run;
        }

        private void PrintMap(params ITaskTestGrain[] grains)
        {
            var map = new string[grains.Length];
            AsyncCompletion.JoinAll(grains.Select((g, i) => g.Label
                .ContinueWith(label => { map[i] = label; })).ToArray()).Wait();
            for (int i = 0; i < grains.Length; i++)
            {
                logger.Info("Grain {0} = {1}", map[i], ((GrainReference)grains[i]).GrainId);
            }
        }

        // todo: [TestMethod]
        public void TaskRandomRepeats()
        {
            for (var i = 0; i < 10; i++)
            {
                RunAll();
            }
        }

        // todo: reenable when UseChessMessaging works [TestMethod]
        public void TaskBasics()
        {
            logger.Info("Begin TaskBasics");
            if (MyOptions.UseStore)
                primary.OnCommit(RemoteValidator("G1", "Do12"));

            var g3 = TaskTestGrainFactory.CreateGrain(Label: "G3");
            var g2 = TaskTestGrainFactory.CreateGrain(Label: "G2", Children: new List<ITaskTestGrain> {g3});
            var g4 = TaskTestGrainFactory.CreateGrain(Label: "G4", Children: new List<ITaskTestGrain> {g3});
            var g1 = TaskTestGrainFactory.CreateGrain(Label: "G1", Children: new List<ITaskTestGrain> {g2, g4});

            PrintMap(g1, g2, g3, g4);

            g1.Do12()
                .Wait();

            Validate("G1", "Do12");
        }


        // todo: [TestMethod]
        public void TaskIsolation()
        {
            logger.Info("Begin TaskIsolation");
            if (MyOptions.UseStore)
                primary.OnCommit(RemoteValidator("G1", "Do12", "G5", "Do1", "G1", "Do3"));

            var g3 = TaskTestGrainFactory.CreateGrain(Label: "G3");
            var g2 = TaskTestGrainFactory.CreateGrain(Label: "G2", Children: new List<ITaskTestGrain> { g3 });
            var g4 = TaskTestGrainFactory.CreateGrain(Label: "G4", Children: new List<ITaskTestGrain> { g3 });
            var g1 = TaskTestGrainFactory.CreateGrain(Label: "G1", Children: new List<ITaskTestGrain> { g2, g4, g3 });
            var g5 = TaskTestGrainFactory.CreateGrain(Label: "G5", Children: new List<ITaskTestGrain> { g4 });

            AsyncCompletion.JoinAll(new [] {g1, g2, g3, g4, g5}).Wait();
            AsyncCompletion.JoinAll(new[]
            {
                g1.Do12(),
                g5.Do1(),
                g1.Do3()
            })
                .Wait();

            Validate("G1", "Do12", "G5", "Do1", "G1", "Do3"); 
        }

        // todo: [TestMethod]
        public void TaskConsistency()
        {
            logger.Info("Begin TaskConsistency");
            if (MyOptions.UseStore)
                primary.OnCommit(RemoteValidator("G1", "Do12Parallel"));

            var g3 = TaskTestGrainFactory.CreateGrain(Label: "G3");
            var g2 = TaskTestGrainFactory.CreateGrain(Label: "G2", Children: new List<ITaskTestGrain> { g3 });
            var g4 = TaskTestGrainFactory.CreateGrain(Label: "G4", Children: new List<ITaskTestGrain> { g3 });
            var g1 = TaskTestGrainFactory.CreateGrain(Label: "G1", Children: new List<ITaskTestGrain> { g2, g4 });

            g1.Do12Parallel()
                .Wait();

            Validate("G1", "Do12Parallel");
        }

        // todo: distributed deadlock detection and/or auto-activation creation
        // [TestMethod]
        public void TaskAtomicity()
        {
            logger.Info("Begin TaskAtomicity");
            if (MyOptions.UseStore)
                primary.OnCommit(RemoteValidator("G1", "Do12", "G4", "Do12", "G6", "Do123"));

            var g3 = TaskTestGrainFactory.CreateGrain(Label: "G3");
            var g2 = TaskTestGrainFactory.CreateGrain(Label: "G2");
            var g1 = TaskTestGrainFactory.CreateGrain(Label: "G1", Children: new List<ITaskTestGrain> { g2, g3 });
            var g5 = TaskTestGrainFactory.CreateGrain(Label: "G5");
            var g4 = TaskTestGrainFactory.CreateGrain(Label: "G4", Children: new List<ITaskTestGrain> { g3, g5 });
            var g6 = TaskTestGrainFactory.CreateGrain(Label: "G6", Children: new List<ITaskTestGrain> { g2, g5, g3 });

            AsyncCompletion.JoinAll(new[]
            {
                g1.Do12(),
                g4.Do12(),
                g6.Do123()
            })
                .Wait();

            Validate("G1", "Do12", "G4", "Do12", "G6", "Do123");
        }

        // todo: distributed deadlock detection and/or auto-activation creation
        // [TestMethod]
        public void TaskDurability()
        {
            logger.Info("Begin TaskDurability");
            if (MyOptions.UseStore)
                primary.OnCommit(RemoteValidator("G1", "Do12", "G4", "Do12", "G6", "Do1"));

            var g3 = TaskTestGrainFactory.CreateGrain(Label: "G3");
            var g2 = TaskTestGrainFactory.CreateGrain(Label: "G2");
            var g1 = TaskTestGrainFactory.CreateGrain(Label: "G1", Children: new List<ITaskTestGrain> { g2, g3 });
            var g5 = TaskTestGrainFactory.CreateGrain(Label: "G5");
            var g4 = TaskTestGrainFactory.CreateGrain(Label: "G4", Children: new List<ITaskTestGrain> { g3, g5 });
            var g6 = TaskTestGrainFactory.CreateGrain(Label: "G6", Children: new List<ITaskTestGrain> { g5 });

            AsyncCompletion.JoinAll(new[]
            {
                g1.Do12(),
                g4.Do12(),
                g6.Do1()
            })
                .Wait();

            Validate("G1", "Do12", "G4", "Do12", "G6", "Do1");
        }
    }


    internal class Flow
    {
        public GrainId Grain { get; set; }
        public string Method { get; set; }
        public int Count { get; set; }

        public KeyValuePair<GrainId, string> Request
        {
            get { return new KeyValuePair<GrainId, string>(Grain, Method); }
        }

        public Flow Times(int factor)
        {
            return new Flow { Grain = Grain, Method = Method, Count = Count * factor };
        }

        public override string ToString()
        {
            return String.Format("{0}.{1}#{2}", Grain, Method, Count);
        }
    }
#endif
}

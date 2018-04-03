using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Samples.Chirper.GrainInterfaces;

namespace Orleans.Samples.Chirper.Network.Loader
{
    /// <summary>
    /// Read a GraphML data file for the Chirper network and create appropriate Orleans grains to represent that data.
    /// </summary>
    public class ChirperNetworkLoader
    {
        private const int ProgressInterval = 1000;
        private const int DefaultPipelineSize = 20;

        public int PipelineSize { get; set; }
        public FileInfo FileToLoad { get; set; }

        public Dictionary<long, IChirperAccount> Users { get; private set; }

        private int numUsers;
        private int numRelationships;
        private int numErrors;
        private int duplicateNodes = 0;

        private AsyncPipeline pipeline;
        private NetworkDataReader loader;
        private List<Task> promises;

        private readonly Stopwatch runtimeStopwatch = new Stopwatch();
        private readonly Stopwatch edgeStopwatch = new Stopwatch();
        private readonly Stopwatch loadStopwatch = new Stopwatch();

        public ChirperNetworkLoader(AsyncPipeline pipeline = null)
        {
            this.pipeline = pipeline;
            this.Users = new Dictionary<long, IChirperAccount>();
            this.PipelineSize = (pipeline == null) ? DefaultPipelineSize : pipeline.Capacity;
            
            if (!GrainClient.IsInitialized)
            {
                var config = ClientConfiguration.LocalhostSilo();
                GrainClient.Initialize(config);
            }
            runtimeStopwatch.Start();
        }

        public async Task<int> Run()
        {
            LogMessage("********************************************************");
            LogMessage("{0}\t{1:G}", "Network Load Starting at:", DateTime.Now);

            LoadData();

            LogMessage("Pipeline={0}", this.PipelineSize);
            if (pipeline == null)
                this.pipeline = new AsyncPipeline(this.PipelineSize);

            try
            {
                loadStopwatch.Start();

                LogMessage("Creating {0} user nodes...", loader.Nodes.Count);
                List<Task> work = CreateUserNodes();
                LogMessage("Waiting for {0} promises to complete.", work.Count);
                await Task.WhenAll(work);
                LogMessage("Finished creating {0} users in {1}", numUsers, runtimeStopwatch.Elapsed);

                LogMessage("Creating {0} user relationship links...", loader.Edges.Count);
                edgeStopwatch.Start();
                LogMessage("Processing user relationship links group #1");
                work = CreateRelationshipEdges(true);
                LogMessage("Waiting for {0} promises to complete.", work.Count);
                await Task.WhenAll(work);
                LogMessage("Processing user relationship links group #2");
                work = CreateRelationshipEdges(false);
                LogMessage("Waiting for {0} promises to complete.", work.Count);
                await Task.WhenAll(work);

                edgeStopwatch.Stop();
                LogMessage("Finished creating {0} user relationship links in {1}", numRelationships, runtimeStopwatch.Elapsed);
            }
            catch(Exception exc)
            {
                ReportError("Error creating Chirper data network", exc);
                throw exc.GetBaseException();
            }

            loadStopwatch.Stop();
            runtimeStopwatch.Stop();

            LogMessage(string.Empty);
            LogMessage("Loading Completed:");
            LogMessage("\t{0} users ({1} duplicates, {2} new)", this.numUsers, this.duplicateNodes, this.numUsers - this.duplicateNodes);
            LogMessage("\t{0} relationship links", this.numRelationships);
            LogMessage("\t{0} errors", this.numErrors);
            LogMessage(string.Empty);
            LogMessage("\tNode Processing Time:\t{0}", loadStopwatch.Elapsed - edgeStopwatch.Elapsed);
            LogMessage("\tEdge Processing Time:\t{0}", edgeStopwatch.Elapsed);
            LogMessage("\tTotal Load Time:\t{0}", loadStopwatch.Elapsed);

            LogMessage(string.Empty);
            LogMessage("Execution Time:\t\t{0}", runtimeStopwatch.Elapsed);
            LogMessage("Network Load Finished at:\t{0:G}", DateTime.Now);
            LogMessage(string.Empty);

            return 0;
        }

        public void LoadData()
        {
            if (FileToLoad == null) throw new ArgumentNullException("FileToLoad", "No load file specified");
            if (!FileToLoad.Exists) throw new FileNotFoundException("Cannot find file to load: " + this.FileToLoad.FullName);

            LogMessage("Loading GraphML file:\t" + FileToLoad.FullName);
            loader = new NetworkDataReader();
            loader.ProgressInterval = ProgressInterval;
            loader.LoadData(FileToLoad);
        }

        public void WaitForCompletion()
        {
            Console.WriteLine("---Press any key to exit---");
            Console.ReadKey();
            LogMessage("Loading Completed: {0} users, {1} relationship links, {2} errors", this.numUsers, this.numRelationships, this.numErrors);
        }

        public bool ParseArguments(string[] args)
        {
            bool ok = true;
            int argPos = 1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-") || a.StartsWith("/"))
                {
                    a = a.Substring(1).ToLowerInvariant();
                    switch (a)
                    {
                        case "pipeline":
                            this.PipelineSize = Int32.Parse(args[++i]);
                            break;
                        case "?":
                        case "help":
                        default:
                            ok = false;
                            break;
                    }
                }
                // unqualified arguments below
                else if (argPos == 1)
                {
                    this.FileToLoad = new FileInfo(a);
                    argPos++;
                }
                else
                {
                    // Too many command line arguments
                    ReportError("Too many command line arguments supplied: " + a);
                    return false;
                }
            }
            return ok;
        }

        public void PrintUsage()
        {
            using (StringWriter usageStr = new StringWriter())
            {
                usageStr.WriteLine(Assembly.GetExecutingAssembly().GetName().Name + ".exe"
                    + " [/pipeline {n}] {file}");
                usageStr.WriteLine("Where:");
                usageStr.WriteLine(" {file}            = GraphML file to load");
                usageStr.WriteLine(" /pipeline {n}     = Request pipeline size [default " + DefaultPipelineSize + "]");

                Console.WriteLine(usageStr.ToString());
            }
        }

        public List<Task> CreateUserNodes()
        {
            TimeSpan intervalStart = runtimeStopwatch.Elapsed;

            promises = loader.ProcessNodes(
                AddChirperUser,
                i =>
                {
                    // Show progress interval breakdown
                    TimeSpan currentRuntime = runtimeStopwatch.Elapsed;
                    LogMessage("Users Created: {0,10}  Time: {1}  Cumulative: {2}",
                        i, currentRuntime - intervalStart, currentRuntime); // Show progress

                    intervalStart = runtimeStopwatch.Elapsed; // Don't include log writing in the interval.
                }, pipeline);
            
            return promises;
        }

        public List<Task> CreateRelationshipEdges(bool forward)
        {
            TimeSpan intervalStart = runtimeStopwatch.Elapsed;

            promises = loader.ProcessEdges((fromUserId, toUserId) =>
            {
                // Segment updates into disjoint update groups, based on source / target node id
                bool skip = forward ? fromUserId > toUserId : fromUserId < toUserId;
                if (skip)
                {
                    return null; // skip this edge for this time round
                }

                IChirperAccount fromUser = this.Users[fromUserId];

                return AddChirperFollower(fromUser, fromUserId, toUserId);
            },
            i =>
            {
                // Show progress interval breakdown
                TimeSpan currentRuntime = runtimeStopwatch.Elapsed;
                LogMessage("Links created: {0,10}  Time: {1}  Cumulative: {2}", i, currentRuntime - intervalStart, currentRuntime);

                intervalStart = runtimeStopwatch.Elapsed; // Don't include log writing in the interval.
            }, pipeline);

            return promises;
        }

        public string DumpStatus()
        {
            int completedTasks = promises.Count(t => t.IsCompleted);
            int faultedTasks = promises.Count(t => t.IsFaulted);
            int cancelledTasks = promises.Count(t => t.IsCanceled);
            int totalTasks = promises.Count;
            int pipelineCount = pipeline.Count;
            string statusDump = string.Format(
                "Promises: Completed={0} Faulted={1} Cancelled={2} Total={3} Unfinished={4} Pipeline={5}",
                completedTasks, faultedTasks, cancelledTasks, totalTasks,
                totalTasks - completedTasks - faultedTasks - cancelledTasks,
                pipelineCount);
            Console.WriteLine(statusDump);
            return statusDump;
        }

        private async Task AddChirperUser(ChirperUserInfo userData)
        {
            // Create Chirper account grain for this user
            long userId = userData.UserId;
            string userAlias = userData.UserAlias;
            IChirperAccount grain = GrainClient.GrainFactory.GetGrain<IChirperAccount>(userId);
            this.Users[userId] = grain;
            try
            {
                // Force creation of the grain by sending it a first message to set user alias
                ChirperUserInfo userInfo = ChirperUserInfo.GetUserInfo(userId, userAlias);
                await grain.SetUserDetails(userInfo);

                Interlocked.Increment(ref numUsers);
            }
            catch (Exception exc)
            {
                ReportError("Error creating user id " + userId, exc);
                throw exc.GetBaseException();
            }
        }

        private async Task AddChirperFollower(IChirperAccount fromUser, long fromUserId, long toUserId)
        {
            // Create subscriptions between two Chirper users
            try
            {
                await fromUser.FollowUserId(toUserId);
                Interlocked.Increment(ref numRelationships);
            }
            catch (Exception exc)
            {
                ReportError(string.Format("Error adding follower relationship from user id {0} to user id {1}", fromUserId, toUserId), exc);
                throw exc.GetBaseException();
            }
        }

        #region Status message capture / logging
        internal void LogMessage(string message, params object[] args)
        {
            if (args.Length > 0)
            {
                message = String.Format(message, args);
            }
            message = $"[{DateTime.UtcNow:s}]   {message}";
            Console.WriteLine(message);
        }
        internal void ReportError(string msg, Exception exc)
        {
            msg = "*****\t" + msg + "\n   --->" + exc;
            ReportError(msg);
        }
        internal void ReportError(string msg)
        {
            Interlocked.Increment(ref numErrors);

            msg = $"Error Time: {DateTime.UtcNow:s}\n\t{msg}";
            Console.WriteLine(msg);
        }
        #endregion
    }
}

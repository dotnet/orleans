using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FortuneCookies;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Samples.Chirper.GrainInterfaces;
using Orleans.Samples.Chirper.Network.Loader;

namespace Orleans.Samples.Chirper.Network.Driver
{
    class ChirperNetworkDriver : IDisposable
    {
        private AsyncPipeline pipeline;
        private readonly List<SimulatedUser> activeUsers;
        
        public FileInfo GraphDataFile { get; internal set; }

        public double LoggedInUserRate { get; set; }
        public double ShouldRechirpRate { get; set; }
        public int ChirpPublishTimebase { get; set; }
        public bool ChirpPublishTimeRandom { get; set; }
        public bool Verbose { get; set; }
        public int LinksPerUser { get; set; }
        public int PipelineLength { get; set; }

        private ChirperNetworkLoader loader;
        private readonly Fortune fortune;
        private ChirperPerformanceCounters perfCounters;

        public ChirperNetworkDriver()
        {
            this.LinksPerUser = 27;
            this.LoggedInUserRate = 0.001;
            this.ShouldRechirpRate = 0.0;
            this.ChirpPublishTimebase = 0;
            this.ChirpPublishTimeRandom = true;
            this.activeUsers = new List<SimulatedUser>();
            this.PipelineLength = 500;
            this.fortune = new Fortune("fortune.txt");

            if (!GrainClient.IsInitialized)
            {
                var config = ClientConfiguration.LocalhostSilo();
                GrainClient.Initialize(config);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification="This method creates SimulatedUser objects which will not be disposed until the Stop method")]
        public int Run()
        {
            this.perfCounters = new ChirperPerformanceCounters(this.GraphDataFile.FullName);
            perfCounters.ChirpsPerSecond.RawValue = 0;

            pipeline = new AsyncPipeline(PipelineLength);
            loader = new ChirperNetworkLoader(pipeline);
            //if (this.Verbose) loader.SetVerbose();

            Console.WriteLine("Loading Chirper network data file " + this.GraphDataFile.FullName);
            loader.FileToLoad = this.GraphDataFile;
            loader.LoadData();
            loader.CreateUserNodes(); // Connect/create users

            Console.WriteLine(
                "Starting Chirper network traffic simulation for {0} users.\n"
                + "Chirp publication time base = {1}\n"
                + "Random time distribution = {2}\n"
                + "Rechirp rate = {3}", 
                loader.Users.Count, this.ChirpPublishTimebase, this.ChirpPublishTimeRandom, this.ShouldRechirpRate);

            ForEachUser(user =>
            {
                SimulatedUser u = new SimulatedUser(user);
                u.ShouldRechirpRate = this.ShouldRechirpRate;
                u.ChirpPublishTimebase = this.ChirpPublishTimebase;
                u.ChirpPublishTimeRandom = this.ChirpPublishTimeRandom;
                u.Verbose = this.Verbose;
                lock (activeUsers)
                {
                    activeUsers.Add(u);
                }
                u.Start();
            });

            Console.WriteLine("Starting sending chirps...");

            Random rand = new Random();
            int count = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();
            do
            {
                int i = rand.Next(activeUsers.Count);
                SimulatedUser u = activeUsers[i];
                if (u == null)
                {
                    Console.WriteLine("User {0} not found.", i);
                    return -1;
                }

                string msg = fortune.GetFortune();

                pipeline.Add(u.PublishMessage(msg));
                count++;
                if (count % 10000 == 0)
                {
                    Console.WriteLine("{0:0.#}/sec: {1} in {2}ms.  Pipeline contains {3} items.",
                        ((float)10000 / stopwatch.ElapsedMilliseconds) * 1000, count, stopwatch.ElapsedMilliseconds, pipeline.Count);
                    perfCounters.ChirpsPerSecond.RawValue = (int) (((float) 10000 / stopwatch.ElapsedMilliseconds) * 1000);

                    stopwatch.Restart();
                }

                if (ChirpPublishTimebase > 0)
                {
                    Thread.Sleep(ChirpPublishTimebase * 1000);
                }
            } while (true);
        }

        public void Stop()
        {
            activeUsers.ForEach(u => u.Dispose());
            activeUsers.Clear();
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
                    a = a.ToLowerInvariant().Substring(1);
                    switch (a)
                    {
                        case "verbose":
                        case "v":
                            this.Verbose = true;
                            break;
                        case "norandom":
                            this.ChirpPublishTimeRandom = false;
                            break;
                        case "time":
                            this.ChirpPublishTimebase = Int32.Parse(args[++i]);
                            break;
                        case "rechirp":
                            this.ShouldRechirpRate = Double.Parse(args[++i]);
                            break;
                        case "links":
                            this.LinksPerUser = Int32.Parse(args[++i]);
                            break;
                        case "pipeline":
                            this.PipelineLength = Int32.Parse(args[++i]);
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
                    this.GraphDataFile = new FileInfo(a);
                    argPos++;

                    if (!GraphDataFile.Exists)
                    {
                        Console.WriteLine("Cannot find data file: " + this.GraphDataFile.FullName);
                        ok = false;
                    }
                }
                else
                {
                    // Too many command line arguments
                    Console.WriteLine("Too many command line arguments supplied: " + a);
                    return false;
                }
            }
            if (GraphDataFile == null)
            {
                Console.WriteLine("No graph data file supplied -- driver cannot run.");
                return false;
            }

            return ok;
        }

        public void PrintUsage()
        {
            using (StringWriter usageStr = new StringWriter())
            {
                usageStr.WriteLine(Assembly.GetExecutingAssembly().GetName().Name + ".exe [options] {file}");
                usageStr.WriteLine("Where:");
                usageStr.WriteLine(" {file}       = GraphML file for network to simulate");
                usageStr.WriteLine(" /create      = Create the network graph in Orleans before running simulation");
                usageStr.WriteLine(" /time {t}    = Base chirp publication time (integer)");
                usageStr.WriteLine(" /rechirp {r} = Rechirp rate (decimal 0.0 - 1.0)");
                usageStr.WriteLine(" /norandom    = Use constant chirp publication time intervals, rather than random");
                usageStr.WriteLine(" /create      = Create the network graph in Orleans before running simulation");
                usageStr.WriteLine(" /v           = Verbose output");

                Console.WriteLine(usageStr.ToString());
            }
        }

        private void ForEachUser(Action<IChirperAccount> action)
        {
            List<Task> promises = new List<Task>();
            foreach (long userId in loader.Users.Keys)
            {
                IChirperAccount user = loader.Users[userId];
                Task p = Task.Factory.StartNew(() => action(user));
                pipeline.Add(p);
                promises.Add(p);
            }
            pipeline.Wait();
            Task.WhenAll(promises).Wait();
        }

        #region IDisposable Members

        public void Dispose()
        {
            //loader.Dispose();
        }

        #endregion
    }
}

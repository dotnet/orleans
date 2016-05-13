using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Samples.Chirper.Network.Generator
{
    class ChirperNetworkGenerator
    {
        private const int Kilo = 1024;
        private const int ProgressInterval = 1000;
        private const string DefaultGraphMLFile = "ChirperNetwork.graphml";

        private string graphMLFile = DefaultGraphMLFile;
        private int networkNodeCount = 50;
        private int networkEdgeCount = 100;
        private int nodeIdStartValue = 1;
        private int edgeIDStartValue = 1;
        private int edgeNodeStartIdValue = 1;
        public bool Automated { get; private set; }
        private bool random = false;
        private List<string> networkGeneratorLog;

        internal bool ParseArguments(string[] args)
        {
            // TODO: Validate the command line parameters:
            //  * Edge count should be greater than or equal to 0, and node count should be greater than zero.
            //  * If file exists, prompt for overwrite.  Add switch for no prompt.
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
                        case "auto":
                            this.Automated = true;
                            break;
                        case "random":
                            this.random = true;
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
                    this.networkNodeCount = int.Parse(a);
                    argPos++;
                }
                else if (argPos == 2)
                {
                    this.networkEdgeCount = int.Parse(a);
                    argPos++;
                }
                else if (argPos == 3)
                {
                    this.graphMLFile = a;
                    argPos++;
                }
                else if (argPos == 4)
                {
                    if (!string.IsNullOrWhiteSpace(a))
                    {
                        if (!int.TryParse(a, out this.nodeIdStartValue))
                        {
                            Console.WriteLine("Could not convert starting node value ({0}) to an int - defaulting to 1.", a);
                            this.nodeIdStartValue = 1;
                        }

                        if (this.nodeIdStartValue + this.networkNodeCount > int.MaxValue)
                        {
                            Console.WriteLine("Cannot generate {0:N0} nodes starting from id {1:N0} because it would exceed the max value for an integer ({2}).", this.networkNodeCount, this.nodeIdStartValue, int.MaxValue);
                            return false;
                        }
                    }
                    argPos++;
                }
                else if (argPos == 5)
                {
                    if (!string.IsNullOrWhiteSpace(a))
                    {
                        if (!int.TryParse(a, out this.edgeIDStartValue))
                        {
                            Console.WriteLine("Could not convert starting edge value ({0}) to an int - defaulting to 1.", a);
                            this.nodeIdStartValue = 1;
                        }

                        if (this.edgeIDStartValue + this.networkEdgeCount > int.MaxValue)
                        {
                            Console.WriteLine("Cannot generate {0:N0} edges starting from id {1:N0} because it would exceed the max value for an integer ({2}).", this.networkEdgeCount, this.edgeIDStartValue, int.MaxValue);
                            return false;
                        }
                    }
                    argPos++;
                }
                else if (argPos == 6)
                {
                    if (!string.IsNullOrWhiteSpace(a))
                    {
                        if (!int.TryParse(a, out this.edgeNodeStartIdValue))
                        {
                            Console.WriteLine("Could not convert the value used as the starting node Id for creating edges ({0}) to an int - defaulting to {1}.", a, nodeIdStartValue);
                            this.edgeNodeStartIdValue = this.nodeIdStartValue;
                        }
                    }
                    argPos++;
                }
                else
                {
                    // Too many command line arguments
                    Console.WriteLine("Too many command line arguments supplied: " + a);
                    return false;
                }
            }
            return ok;
        }

        internal void PrintUsage()
        {
            using (StringWriter usageStr = new StringWriter())
            {
                usageStr.WriteLine(Assembly.GetExecutingAssembly().GetName().Name + ".exe"
                    + " [/auto] [/random] {nodeCount} {edgeCount} {file} {nodeStartId} {edgeStartId} {edgeNodeStartId}");
                usageStr.WriteLine("Where:");
                usageStr.WriteLine(" {nodeCount}       = The number of nodes to generate.  Default is 50.");
                usageStr.WriteLine(" {edgeCount}       = The number of edges to generate.  Default is 100");
                usageStr.WriteLine("                        Set edgeCount to 0 to disable edge generation.");
                usageStr.WriteLine(" {file}            = GraphML file to generate. Default is " + DefaultGraphMLFile);
                usageStr.WriteLine(" {nodeStartId}     = The start Id to use for new nodes.  Default is 1");
                usageStr.WriteLine(" {edgeStartId}     = The start Id to use for new edges.  Default is 1");
                usageStr.WriteLine(" {edgeNodeStartId} = The minimum node Id to use when building new edges.  Default is nodeStartId");
                usageStr.WriteLine("                        Used to build edges that span nodes generated in previously run graphML files.");
                usageStr.WriteLine(" /auto             = Will suppress the prompt to exit.");
                usageStr.WriteLine(" /random           = Causes edges to be generated randomly.");

                Console.WriteLine(usageStr.ToString());
            }
        }

        internal void LogMessage(string message)
        {
            networkGeneratorLog.Add(message);
            Console.WriteLine(message);
        }

        internal int Run()
        {
            string thisProcessName = Process.GetCurrentProcess().ProcessName;
            using (PerformanceCounter memoryMonitor = new PerformanceCounter("Process", "Working Set", thisProcessName))
            {
                memoryMonitor.NextSample();
                using (PerformanceCounter processorMonitor = new PerformanceCounter("Process", "% Processor Time", thisProcessName))
                {
                    processorMonitor.NextSample();
                    networkGeneratorLog = new List<string>();
                    LogMessage("********************************************************");
                    LogMessage(string.Format("{0,25}\t{1:G}", "Start Network Generation:", DateTime.Now));
                    LogMessage(string.Format("{0,25}\t{1:N0}\tEdges: {2:N0}", "Requested Nodes:", this.networkNodeCount, this.networkEdgeCount));
                    LogMessage(string.Format("{0,25}\t{1:N0}\tEdges: {2:N0}", "Start Ids - Nodes:", this.nodeIdStartValue, this.edgeIDStartValue));
                    ChirperGraphMLDocument graphDoc = new ChirperGraphMLDocument();
                    LogMessage(string.Empty);
                    LogMessage("Starting node generation...");

                    TimeSpan nodeGenerationTime = GenerateChirperUserNodes(graphDoc);
                    LogMessage("\tNode generation complete in " + nodeGenerationTime);
                    LogPerformanceCounterData(memoryMonitor, processorMonitor);

                    LogMessage(string.Empty);
                    FlushLog();

                    int duplicateEdgeStatistic = 0;
                    TimeSpan edgeGenerationTime = default(TimeSpan);
                    if (this.networkEdgeCount > 0)
                    {
                        LogMessage("Starting edge generation...");
                        edgeGenerationTime = GenerateChirperFollowerEdge(graphDoc, ref duplicateEdgeStatistic);

                        LogMessage("\tEdge generation complete in " + edgeGenerationTime);
                        LogMessage("\tDuplicates generated: " + duplicateEdgeStatistic);
                        LogPerformanceCounterData(memoryMonitor, processorMonitor);
                        LogMessage(string.Empty);
                    }
                    else
                    {
                        LogMessage("Edge generation suppressed from command line.");
                        LogMessage(string.Empty);
                    }

                    Stopwatch stopwatch = new Stopwatch();
                    FlushLog();

                    LogMessage("Starting GraphML File write...");
                    stopwatch.Restart();
                    graphDoc.WriteXmlWithWriter(this.graphMLFile);
                    stopwatch.Stop();
                    TimeSpan xmlWriteTime = TimeSpan.FromSeconds(stopwatch.Elapsed.TotalSeconds);
                    LogMessage("\tGraphML file write complete in " + xmlWriteTime);
                    LogPerformanceCounterData(memoryMonitor, processorMonitor);

                    LogMessage(string.Empty);
                    LogMessage(string.Format(CultureInfo.InvariantCulture, "{0,30}\t{1}", "Total Graph Generation Time:", (nodeGenerationTime + edgeGenerationTime + xmlWriteTime)));
                    LogMessage(string.Empty);
                    FlushLog();

                    // Get the size of the file so we can add the statistic.
                    FileInfo fileInfo = new FileInfo(this.graphMLFile);
                    fileInfo.Refresh();
                    long fileLength = fileInfo.Length;
                    string range = "bytes";
                    if (fileLength > Kilo)
                    {
                        fileLength = fileLength / Kilo;
                        range = "KB";
                    }
                    if (fileLength > Kilo)
                    {
                        fileLength = fileLength / Kilo;
                        range = "MB";
                    }

                    LogMessage(string.Format(CultureInfo.InvariantCulture, "{0,30}\t{1:N0} {2}", "GraphML File Size:", fileLength, range));

                    LogMessage(string.Empty);
                    FlushLog();

                }
            }

            // Using Linq to XML here to insert the statistic comment won't adversely affect performance.
            //XComment statisticsComment = new XComment(summaryMessageStringBuilder.ToString());
            //XDocument generatedGraphMLDoc = XDocument.Load(graphMLFile);
            //XElement graphElement = generatedGraphMLDoc.Root;
            //graphElement.AddFirst(statisticsComment);
            //graphElement.Document.Save(this.graphMLFile);

            return 0;
        }

        /// <summary>
        /// Moves the collected log entries to the file and clears the log for the next batch.
        /// </summary>
        internal void FlushLog()
        {
            try
            {
                File.AppendAllLines("NetworkGeneratorLog.txt", networkGeneratorLog);
                networkGeneratorLog.Clear();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not write log file; will try again at next milestone.\n Exception:" + ex.Message);
            }

        }

        private void LogPerformanceCounterData(PerformanceCounter memoryMonitor, PerformanceCounter processorMonitor)
        {
            LogMessage(string.Format("\t\tMemory Usage:\t{0,15:N0}", memoryMonitor.NextSample().RawValue));
            LogMessage(string.Format("\t\tProcessor Usage:{0,15:N0}", processorMonitor.NextSample().RawValue));
        }

        /// <summary>
        /// Generates source-target pairs among the previously generated Chirper users.  
        /// </summary>
        /// <param name="graphDoc">The GraphML document that the code is building.</param>
        /// <param name="duplicateEdgeStatistic">
        ///     A passed in statistic that will be incremented each time a duplicate edge is generated.
        /// </param>
        /// <returns>The length of time used to generate the edges among the nodes.</returns>
        private TimeSpan GenerateChirperFollowerEdge(ChirperGraphMLDocument graphDoc, ref int duplicateEdgeStatistic)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            int edgeId = this.edgeIDStartValue;
            int maxNodeId = this.nodeIdStartValue + this.networkNodeCount;
            int minNodeId = this.edgeNodeStartIdValue;
            Random randomGenerator = new Random();

            Console.Write("\t");
            while (edgeId < this.networkEdgeCount + this.edgeIDStartValue)
            {
                int source = 0;
                int target = 0;
                if (this.random)
                {
                    // Use while loops to prevent keeping source and target values of zero.
                    while (source == 0)
                    {
                        source = randomGenerator.Next(this.nodeIdStartValue, maxNodeId);
                    }

                    while (target == 0)
                    {
                        target = randomGenerator.Next(this.nodeIdStartValue, maxNodeId);
                    }
                }
                else
                {
                    int relativeEdgeId = edgeId - this.edgeIDStartValue;
                    int edgesPerNode = this.networkEdgeCount / this.networkNodeCount;
                    int relativeSource = relativeEdgeId / edgesPerNode;
                    int relativeTarget = (relativeSource + 1 + (relativeEdgeId % edgesPerNode));
                    int range = maxNodeId - minNodeId;
                    source = relativeSource + minNodeId;
                    target = (relativeTarget % range) + minNodeId;
                }

                // Don't allow the source to be equal to the target.
                while (source == target)
                {
                    target = randomGenerator.Next(minNodeId, maxNodeId);
                }
                if (graphDoc.AddEdge(edgeId, source, target))
                {
                    // Don't increment if the add fails due to a duplicate.
                    edgeId++;
                }
                else
                {
                    duplicateEdgeStatistic++;
                }

                if ((edgeId % ProgressInterval) == 0) Console.Write("."); // Show progress
            }
            Console.WriteLine(".");
            stopwatch.Stop();
            return TimeSpan.FromSeconds(stopwatch.Elapsed.TotalSeconds);
        }


        /// <summary>
        /// Generates Chirper "User Ids" and adds them to the GraphML document.
        /// </summary>
        /// <param name="graphDoc">The GraphML document that the code is building.</param>
        /// <returns>The length of time used to generate the nodes.</returns>
        private TimeSpan GenerateChirperUserNodes(ChirperGraphMLDocument graphDoc)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Console.Write("\t");
            for (int nodeIndex = this.nodeIdStartValue; nodeIndex < this.networkNodeCount + nodeIdStartValue; nodeIndex++)
            {
                // Since we are using a for loop, these will never be duplicates.
                // If the method of generating the indexes is changed to one that could produce duplicates,
                // the return value of AddNode() must be taken into account.
                string userId = "U" + nodeIndex.ToString(CultureInfo.InvariantCulture);
                graphDoc.AddNode(nodeIndex, userId);
                if ((nodeIndex % ProgressInterval) == 0) Console.Write("."); // Show progress
            }
            Console.WriteLine(".");
            stopwatch.Stop();
            TimeSpan nodeGenerationTime = TimeSpan.FromSeconds(stopwatch.Elapsed.TotalSeconds);
            return nodeGenerationTime;
        }

    }
}

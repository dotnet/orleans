using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using LoadTestGrainInterfaces;
using Orleans;


namespace GrainBenchmarkLoadTest
{

    /// <summary>
    /// Used for dynamic graph workload
    /// </summary>
    public class Event
    {
        public Event()
        {
            type = "new player";
        }
        public Event(long n)
        {
            type = "game over";
            game = n;
        }
        public string type;
        public long game;
    }

    /// <summary>
    /// Dynamic graph used for player session workload
    /// Create a thread to execute the run method, and then the gameToPlayers object stores the dynamic graph which changes over time
    /// </summary>
    public class DynamicGraph
    {
        private double startVirtualTime;
        private double stopTime;

        private double playerArrivalRate;
        private double averageGameTime;
        public DynamicGraph(double startVirtualTime, double stopTime, double desiredNumberOfPlayers, double averageGameTime)
        {
            this.startVirtualTime = startVirtualTime;
            this.stopTime = stopTime;
            this.averageGameTime = averageGameTime;

            // Little's Law, fixed average of 4 games per player
            playerArrivalRate = desiredNumberOfPlayers / (4 * averageGameTime);
        }

        List<long> games = new List<long>();
        Random otherrand = new Random();

        public void getGame(out long game, out List<long> players)
        {
            game = 0;
            players = null;
            bool notFound = true;
            lock (games)
            {
                if (games.Count > 0)
                {
                    game = games[otherrand.Next(games.Count)];
                    if (gameToPlayers.TryGetValue(game, out players))
                    {
                        notFound = false;
                    }
                }
                while (gameToPlayers.Count == 0) 
                { 
                    // hope this doesn't happen
                }
                while (notFound)
                {
                    games.Clear();
                    foreach (var v in gameToPlayers)
                    {
                        games.Add(v.Key);
                    }

                    game = games[otherrand.Next(games.Count)];
                    if (gameToPlayers.TryGetValue(game, out players))
                    {
                        notFound = false;
                    }
                }
            }

        }

        private double Exponential(double rate)
        {
            double uniform = rand.NextDouble();
            while (uniform == 1) // just to be safe, can't do Log(0)
            {
                uniform = rand.NextDouble();
            }
            double exponential = Math.Log(1 - uniform) / (-1.0 * rate);
            return exponential;
        }

        public ConcurrentDictionary<long, List<long>> gameToPlayers = new ConcurrentDictionary<long, List<long>>();
        private SortedDictionary<double, List<Event>> events = new SortedDictionary<double, List<Event>>();

        public int activePlayers()
        {
            return gameToPlayers.Count * 8;
        }

        
        public void addEvent(double delay, Event e)
        {
            double nextTime = delay + virtualMinutes;
            List<Event> temp;
            if (!events.TryGetValue(nextTime, out temp))
            {
                temp = new List<Event>();
                events.Add(nextTime, temp);
            }
            temp.Add(e);
        }

        // must have constant seed, all clients need to produce same random values
        private Random rand = new Random(1);
        public double virtualMinutes;

        public bool stopDynamic = false;

        public void run()
        {
            virtualMinutes = startVirtualTime;
            addEvent(0, new Event());
            List<long> playerPool = new List<long>();
            int id = 1;
            Dictionary<long, int> playerGames = new Dictionary<long, int>();
            Stopwatch actualTimer = new Stopwatch();

            actualTimer.Start();
            double realMinutes = 1.0 * actualTimer.ElapsedMilliseconds / 60 / 1000;

            // process events
            while (events.Count > 0 && !stopDynamic)
            {
                if (events.First().Key < realMinutes)
                {
                    virtualMinutes = events.First().Key;

                    if (virtualMinutes >= stopTime)
                    {
                        break;
                    }

                    // process all events for current time (usually always just 1 event)
                    foreach (Event e in events.First().Value)
                    {

                        // new player will be added to player pool, pick a number of games they will play, and a new event will be added for next player
                        if (e.type == "new player")
                        {
                            playerPool.Add(id);
                            playerGames.Add(id, rand.Next(3) + 3);

                            id++;
                            addEvent(Exponential(playerArrivalRate), new Event()); // X rate corresponds to 100*X average players in system
                        }

                        // players in game put back in player pool or removed if their last game
                        if (e.type == "game over")
                        {
                            List<long> players;
                            if (gameToPlayers.TryRemove(e.game, out players))
                            {
                                foreach (int player in players)
                                {
                                    if (playerGames[player] > 0)
                                    {
                                        playerPool.Add(player);
                                    }
                                }
                            }
                        }
                    }

                    events.Remove(events.First().Key);

                    // check whether a new game should be created
                    while (playerPool.Count > 1000)
                    {
                        // gather random set of players to play game
                        List<long> players = new List<long>();
                        for (int i = 0; i < 8; i++)
                        {
                            long player = playerPool[rand.Next(playerPool.Count)];
                            playerPool.Remove(player);
                            players.Add(player);
                            playerGames[player]--;
                        }
                        gameToPlayers.AddOrUpdate(id, players, (k, v) => players);
                        addEvent(rand.NextDouble() * 10 + averageGameTime - 5, new Event(id));
                        id++;
                    }

                    // catch real-time up to virtual time
                    double owedMilliseconds = (virtualMinutes - realMinutes) * 60 * 1000;
                    if (owedMilliseconds > 1)
                    {
                        Thread.Sleep((int)owedMilliseconds);
                    }
                }
                realMinutes = 1.0 * actualTimer.ElapsedMilliseconds / 60 / 1000;
            }
        }
    }

    /// <summary>
    /// Used for graph workloads
    /// </summary>
    public class GrainBenchmarkGraph
    {
        /// <summary>
        /// Utility to get Guid from long value
        /// </summary>
        /// <param name="v">long value</param>
        /// <returns>associated Guid value</returns>
        public static Guid LongToGuid(long v)
        {
            byte[] longBytes = BitConverter.GetBytes(v);
            byte[] zeroBytes = BitConverter.GetBytes((long)0);
            byte[] guidBytes = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                guidBytes[i] = longBytes[i];
                guidBytes[i + 8] = zeroBytes[i];
            }
            return new Guid(guidBytes);
        }

        private Random rand;
        private Dictionary<long, Guid> nodeToGuid;
        private HashSet<long> vertices;
        private List<Guid> createdVertices;
        private int numEdges;
        private Dictionary<long, HashSet<long>> edges;

        private Dictionary<long, int> degree;
        private Dictionary<int, long> cumulativeDegreeToVertex;
        private List<int> cumulativeDegree;

        /// <summary>
        /// Useful for obtaining grains we need to warm up in the load test, these are the extra grains based on graph creation
        /// </summary>
        /// <returns>List of grain guids</returns>
        public List<Guid> GetOtherVertices()
        {
            return createdVertices;
        }

        /// <summary>
        /// Initialize datastructures for graph object
        /// </summary>
        /// <param name="grains">initial grains to be added as vertices, this set of grains will be first targets by the clients</param>
        private GrainBenchmarkGraph(List<IBenchmarkLoadGrain> grains)
        {
            degree = new Dictionary<long, int>();
            numEdges = 0;
            rand = new Random();
            nodeToGuid = new Dictionary<long, Guid>();
            vertices = new HashSet<long>();
            createdVertices = new List<Guid>();
            edges = new Dictionary<long, HashSet<long>>();
            cumulativeDegree = null;
            cumulativeDegreeToVertex = null;
            for (long i = 0; i < grains.Count; i++)
            {
                AddVertex(i, grains[(int)i].GetPrimaryKey());
            }
        }
        

        /// <summary>
        /// Create a star graph, numerous stars
        /// </summary>
        /// <param name="grains">centers for each star</param>
        /// <param name="starNeighbors">number of neighboring nodes for each star</param>
        /// <returns></returns>
        public static GrainBenchmarkGraph CreateStarGraph(List<IBenchmarkLoadGrain> grains, int starNeighbors)
        {
            GrainBenchmarkGraph graph = new GrainBenchmarkGraph(grains);
            for (long j = 0; j < grains.Count; j++)
            {
                for (int i = 0; i < starNeighbors; i++)
                {
                    graph.AddEdge(j, graph.AddVertex());
                }
            }
            return graph;
        }

        /// <summary>
        /// Create a file graph, edges determined based on contents of file which name is passed to this method
        /// </summary>
        /// <param name="grains">Grains which are first contacted by clients, these are assigned to the first vertices seen in the graph file</param>
        /// <param name="file">Filename where contents have an edge on each line where each line has two vertices that are space-separated</param>
        /// <returns></returns>
        public static GrainBenchmarkGraph CreateFileGraph(List<IBenchmarkLoadGrain> grains, string file) 
        {
            GrainBenchmarkGraph graph = new GrainBenchmarkGraph(grains);

            // On the client machines, this file should be in the same directory that invoked the client, so we should to throw away the absolute path part
            StreamReader sr = new StreamReader(Path.GetFileName(file));

            Dictionary<string, int> values = new Dictionary<string, int>();
            string line = sr.ReadLine();
            while (line != null)
            {
                string[] parts = line.Split(' ');
                if (!values.ContainsKey(parts[0]))
                {
                    values.Add(parts[0], values.Count);
                }
                if (!values.ContainsKey(parts[1]))
                {
                    values.Add(parts[1], values.Count);
                }
                graph.AddEdge(values[parts[0]], values[parts[1]]);
                line = sr.ReadLine();
            }
            return graph;
        }

        /// <summary>
        /// Datastructures computed for efficiently calculating a random weighted vertex
        /// This must be called after the graph is fully created and before using these datastructures
        /// </summary>
        private void Initialize()
        {
            if (cumulativeDegree == null)
            {
                lock (this)
                {
                    if (cumulativeDegree == null)
                    {
                        cumulativeDegreeToVertex = new Dictionary<int, long>();
                        cumulativeDegree = new List<int>();
                        int cumulative = 0;
                        foreach (var v in degree)
                        {
                            cumulative += v.Value;
                            cumulativeDegreeToVertex.Add(cumulative, v.Key);
                            cumulativeDegree.Add(cumulative);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adds a vertex with a specific long and Guid if it does not exist
        /// </summary>
        /// <param name="v">vertex to add</param>
        /// <param name="g">Guid to assign that vertex</param>
        private void AddVertex(long v, Guid g)
        {
            if (!vertices.Contains(v))
            {
                degree.Add(v, 0);
                edges.Add(v, new HashSet<long>());
                vertices.Add(v);
                nodeToGuid.Add(v, g);
            }
        }

        /// <summary>
        /// Adds a vertex with a specific long if it does not exist
        /// </summary>
        /// <param name="v">vertex to add</param>
        private void AddVertex(long v)
        {
            if (!vertices.Contains(v))
            {
                AddVertex(v,LongToGuid(v));
            }
        }

        /// <summary>
        /// Adds a vertex with the lowest available long
        /// </summary>
        /// <returns></returns>
        private long AddVertex()
        {
            long v = 0;
            while (vertices.Contains(v))
            {
                v++;
            }
            AddVertex(v);
            createdVertices.Add(nodeToGuid[v]);
            return v;
        }

        /// <summary>
        /// Adds an undirected edge between two vertices
        /// </summary>
        /// <param name="from">adjacent vertex</param>
        /// <param name="to">other adjacent vertex</param>
        private void AddEdge(long from, long to)
        {
            AddVertex(from);
            AddVertex(to);
            if (!edges[from].Contains(to))
            {
                numEdges++;
                degree[from]++;
                degree[to]++;
            }
            edges[from].Add(to);
            edges[to].Add(from);
        }

        /// <summary>
        /// Gets a vertex weighted by its degree, useful for sampling such that each EDGE is chosen uniformly at random
        /// </summary>
        /// <returns>random vertex</returns>
        public long GetWeightedRandom()
        {
            Initialize();
            int randVal;
            lock (rand)
            {
                randVal = rand.Next(numEdges * 2);
            }
            int cumulativeValue = cumulativeDegree.BinarySearch(randVal);
            if (cumulativeValue < 0)
            {
                cumulativeValue = ~cumulativeValue;
            }
            int x = cumulativeDegree[cumulativeValue];
            return cumulativeDegreeToVertex[x];
        }

        /// <summary>
        /// TODO: simple random walk implementation
        /// </summary>
        /// <param name="index">starting vertex</param>
        /// <param name="num">walk length</param>
        /// <returns></returns>
        public Guid[] GetRandomWalk(long index, int num)
        {
            return null;
        }

        /// <summary>
        /// Retrieves Guids of all neighbors of a vertex
        /// </summary>
        /// <param name="index">vertex which we want neighbors of</param>
        /// <returns>neighbors</returns>
        public Guid[] GetAllNeighbors(long index)
        {
            Guid[] neighbors = new Guid[edges[index].Count];
            int i = 0;
            foreach(var v in edges[index])
            {
                neighbors[i++] = nodeToGuid[v];
            }
            return neighbors;
        }

        /// <summary>
        /// Retrieves Guids of a random set of neighbors, chosen uniformly at random
        /// </summary>
        /// <param name="index">vertex which we want neigbhors of</param>
        /// <param name="num">number of neighbors we want</param>
        /// <returns>neighbors</returns>
        public Guid[] GetRandomNeighborsWithDuplicates(long index, int num)
        {
            List<long> toPick = new List<long>(edges[index]);
            Guid[] players = new Guid[num];
            lock (rand)
            {
                for (int i = 0; i < num; i++)
                {
                    players[i] = nodeToGuid[toPick[rand.Next(toPick.Count)]];
                }
            }
            return players;
        }
    }
}

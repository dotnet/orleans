using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Orleans.Transactions.DeadlockDetection
{
    internal class WaitForGraph
    {
        private enum NodeType
        {
            Resource, Transaction
        }

        private readonly struct Node
        {
            private readonly uint idAndFlag;
            private readonly ParticipantId participantId;
            private readonly Guid transactionId;

            public int Id => (int) (this.idAndFlag >> 1);
            public bool IsTransaction => (this.idAndFlag & 1) == 0;
            public bool IsResource => (this.idAndFlag & 1) == 1;

            public ParticipantId ParticipantId => this.IsResource ? this.participantId : default;
            public Guid TransactionId => this.IsTransaction ? this.transactionId : default;

            public Node(Guid transactionId, int id)
            {
                this.idAndFlag = (uint)(id << 1);
                this.participantId = default;
                this.transactionId = transactionId;
            }

            public Node(ParticipantId resourceId, int id)
            {
                this.idAndFlag = (uint)(id << 1) | 1;
                this.transactionId = default;
                this.participantId = resourceId;
            }
        }

        private Dictionary<int, HashSet<int>> edges = new Dictionary<int, HashSet<int>>();
        private Dictionary<int, HashSet<int>> backEdges = new Dictionary<int, HashSet<int>>();
        private Node[] nodes;
        private Dictionary<Guid, int> nodesByTx = new Dictionary<Guid, int>();
        private Dictionary<ParticipantId, int> nodesByRes = new Dictionary<ParticipantId, int>();

        public WaitForGraph(IEnumerable<LockInfo> locks)
        {
            this.InitFromLocks(locks);
        }

        private WaitForGraph(Node[] nodes, Dictionary<int, HashSet<int>> edges, Dictionary<int, HashSet<int>> backEdges,
            Dictionary<Guid, int> nodesByTx, Dictionary<ParticipantId, int> nodesByRes)
        {
            this.nodes = nodes;
            this.edges = edges;
            this.backEdges = backEdges;
            this.nodesByTx = nodesByTx;
            this.nodesByRes = nodesByRes;
        }

        public WaitForGraph GetConnectedSubGraph(IEnumerable<Guid> transactions, IEnumerable<ParticipantId> resources)
        {
            var aroundNodes = transactions.Select(t => this.nodesByTx[t])
                .Concat(resources.Select(r => this.nodesByRes[r]));
            var subGraph = this.GetConnectedSubGraph(aroundNodes);
            return this.CreateSubGraph(subGraph);
        }

        private WaitForGraph CreateSubGraph(IEnumerable<int> includeNodes)
        {
            var includeNodeList = new List<int>(includeNodes);
            var nodeIdMap = new Dictionary<int, int>();
            var nodeList = new List<Node>(includeNodeList.Count);
            var newNodesByTx = new Dictionary<Guid, int>();
            var newNodesByRes = new Dictionary<ParticipantId, int>();
            foreach (var oldNodeId in includeNodeList)
            {
                var newNodeId = nodeList.Count;
                ref var oldNode = ref this.nodes[oldNodeId];
                nodeIdMap[oldNodeId] = newNodeId;
                if (oldNode.IsResource)
                {
                    nodeList.Add(new Node(oldNode.ParticipantId, newNodeId));
                    newNodesByRes[oldNode.ParticipantId] = newNodeId;
                }
                else
                {
                    nodeList.Add(new Node(oldNode.TransactionId, newNodeId));
                    newNodesByTx[oldNode.TransactionId] = newNodeId;
                }
            }

            var newEdges = new Dictionary<int, HashSet<int>>();
            var newBackEdges = new Dictionary<int, HashSet<int>>();
            foreach (var oldNodeId in includeNodeList)
            {
                if (this.edges.TryGetValue(oldNodeId, out var oldEdges))
                {
                    var newSrcId = nodeIdMap[oldNodeId];
                    var set = newEdges[newSrcId] = new HashSet<int>();
                    foreach (var id in oldEdges)
                    {
                        if (nodeIdMap.TryGetValue(id, out var newId))
                        {
                            set.Add(newId);
                        }
                    }
                }
                if (this.backEdges.TryGetValue(oldNodeId, out var oldBackEdges))
                {
                    var newSrcId = nodeIdMap[oldNodeId];
                    var set = newBackEdges[newSrcId] = new HashSet<int>();
                    foreach (var id in oldBackEdges)
                    {
                        if (nodeIdMap.TryGetValue(id, out var newId))
                        {
                            set.Add(newId);
                        }
                    }
                }
            }

            return new WaitForGraph(nodeList.ToArray(), newEdges, newBackEdges, newNodesByTx, newNodesByRes);
        }

        private static bool AddToSet<K, T>(Dictionary<K, HashSet<T>> dict, K key, T value)
        {
            if (!dict.TryGetValue(key, out var set))
            {
                set = new HashSet<T>();
                dict[key] = set;
            }

            return set.Add(value);
        }
        private void InitFromLocks(IEnumerable<LockInfo> locks)
        {
            var nodeList = new List<Node>();
            foreach (var lk in locks)
            {
                if (!this.nodesByRes.TryGetValue(lk.Resource, out var resId))
                {
                    resId = nodeList.Count;
                    this.nodesByRes[lk.Resource] = resId;
                    nodeList.Add(new Node(lk.Resource, resId));
                }

                if (!this.nodesByTx.TryGetValue(lk.TxId, out var txId))
                {
                    txId = nodeList.Count;
                    this.nodesByTx[lk.TxId] = txId;
                    nodeList.Add(new Node(lk.TxId, txId));
                }

                if (lk.IsWait)
                {
                    AddToSet(this.edges, txId, resId);
                    AddToSet(this.backEdges, resId, txId);
                }
                else
                {
                    AddToSet(this.edges, resId, txId);
                    AddToSet(this.backEdges, txId, resId);
                }
            }

            this.nodes = nodeList.ToArray();
        }

        private IEnumerable<int> GetAllEdges(int nodeId)
        {
            var e = this.edges.TryGetValue(nodeId, out var es) ? es : Enumerable.Empty<int>();
            var b = this.backEdges.TryGetValue(nodeId, out es) ? es : Enumerable.Empty<int>();
            return e.Concat(b);
        }

        private IEnumerable<int> GetEdges(int nodeId)
        {
            return this.edges.TryGetValue(nodeId, out var e) ? e : Enumerable.Empty<int>();
        }

        private IList<int> GetConnectedSubGraph(IEnumerable<int> nodeIds)
        {
            var frontier = new Stack<int>();
            var visited = new BitArray(this.nodes.Length);
            foreach(var id in nodeIds) frontier.Push(id);
            var connected = new List<int>();
            while (frontier.Count != 0)
            {
                var currentId = frontier.Pop();
                if (visited[currentId]) continue;

                connected.Add(currentId);
                visited[currentId] = true;
                foreach (var id in this.GetAllEdges(currentId))
                {
                    if (!visited[id])
                    {
                        frontier.Push(id);
                    }
                }
            }
            return connected;
        }

        public IList<LockInfo> ToLockKeys()
        {
            var keys = new HashSet<LockInfo>();
            foreach (var edge in this.edges)
            {
                ref var head = ref this.nodes[edge.Key];
                if (head.IsTransaction)
                {
                    foreach (var tailId in edge.Value)
                    {
                        ref var tail = ref this.nodes[tailId];
                        Debug.Assert(tail.IsResource, "malformed WFG");
                        keys.Add(LockInfo.ForWait(tail.ParticipantId, head.TransactionId));
                    }
                }
                else
                {
                    foreach (var tailId in edge.Value)
                    {
                        ref var tail = ref this.nodes[tailId];
                        Debug.Assert(tail.IsTransaction, "malformed WFG");
                        keys.Add(LockInfo.ForLock(head.ParticipantId, tail.TransactionId));
                    }
                }
            }

            return keys.ToArray();
        }

        // Returns true if any changes occurred
        public bool MergeWith(WaitForGraph other, out WaitForGraph merged) => this.MergeWith(other.ToLockKeys(), out merged);

        public bool MergeWith(IEnumerable<LockInfo> locks, out WaitForGraph merged)
        {
            var lockSet = new HashSet<LockInfo>(locks);
            var myLocks = new HashSet<LockInfo>(this.ToLockKeys());
            bool changed = false;
            foreach (var lockKey in lockSet)
            {
                if (myLocks.Add(lockKey)) changed = true;
            }

            if (!changed)
            {
                merged = this;
                return false;
            }

            merged = new WaitForGraph(myLocks);
            return true;
        }

        public bool DetectCycles(out IList<IList<LockInfo>> locksInCycles)
        {
            var cycles = this.DetectCycles();
            if (cycles.Count == 0)
            {
                locksInCycles = Array.Empty<IList<LockInfo>>();
                return false;
            }

            locksInCycles = cycles.Select(this.CycleToLocks).ToList();
            return true;
        }

        private IList<LockInfo> CycleToLocks(IList<int> cycle)
        {
            var locks = new List<LockInfo>(cycle.Count + 1); // we include the back edge
            for (int i = 0; i < cycle.Count; i++)
            {
                ref var current = ref this.nodes[cycle[i]];
                ref var next = ref this.nodes[i + 1 >= cycle.Count ? cycle[0] : cycle[i + 1]];

                if (current.IsTransaction && next.IsResource)
                {
                    locks.Add(LockInfo.ForLock(next.ParticipantId, current.TransactionId));
                }
                else if (current.IsResource && next.IsTransaction)
                {
                    locks.Add(LockInfo.ForWait(current.ParticipantId, next.TransactionId));
                }
            }

            return locks;
        }

        // book keeping for the detect cycles method below.
        private struct NodeInfo
        {
            public int Index;
            public int MinLink;
            public bool OnStack;
        }

        private List<List<int>> DetectCycles()
        {
            // this uses tarjan's scc algorithm - any scc with a single vertex can be discarded, because
            // vertex's cannot have edges to themselves in our wfg construction (all edges are tx -> res or res -> tx).

            var stack = new Stack<int>();
            var nextIndex = 1; // we use 0 to indicate that a node hasn't been visited
            var info = new NodeInfo[this.nodes.Length];
            var cycles = new List<List<int>>();

            for (var i = 0; i < this.nodes.Length; i++)
            {
                if (info[i].Index == 0)
                {
                    StrongConnect(i);
                }
            }

            void StrongConnect(int v)
            {
                info[v] = new NodeInfo {Index = nextIndex, MinLink = nextIndex, OnStack = true};
                nextIndex++;
                stack.Push(v);

                foreach (var w in this.GetEdges(v))
                {
                    if (info[w].Index == 0)
                    {
                        StrongConnect(w);
                        info[v].MinLink = Math.Min(info[v].MinLink, info[w].MinLink);
                    }
                    else if (info[w].OnStack)
                    {
                        info[v].MinLink = Math.Min(info[v].MinLink, info[w].Index);
                    }
                }

                if (info[v].MinLink == info[v].Index)
                {
                    var scc = new List<int>();
                    int w;
                    do
                    {
                        w = stack.Pop();
                        info[w].OnStack = false;
                        scc.Add(w);
                    } while (w != v);

                    if (scc.Count > 1)
                    {
                        cycles.Add(scc);
                    }
                }
            }

            return cycles;
        }

    }
}
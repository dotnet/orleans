using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Orleans.Transactions.DeadlockDetection
{
    public class WaitForGraph
    {
        private enum Color { White, Gray, Black };

        public struct Node
        {
            private Guid? transactionId;
            private ParticipantId? resourceId;

            private Node(Guid? transactionId, ParticipantId? resourceId)
            {
                this.transactionId = transactionId;
                this.resourceId = resourceId;
            }

            public static Node ForTransaction(Guid txId) => new Node(txId, null);
            public static Node ForResource(ParticipantId resourceId) => new Node(null, resourceId);

            public bool IsTransaction => this.transactionId != null;
            public bool IsResource => this.resourceId != null;

            // ReSharper disable once PossibleInvalidOperationException
            public Guid TransactionId => this.transactionId.Value;
            // ReSharper disable once PossibleInvalidOperationException
            public ParticipantId ResourceId => this.resourceId.Value;

            public bool Equals(Node other) => IsTransaction
                ? other.IsTransaction && other.TransactionId == TransactionId
                  // TODO Implement fast equality for ParticipantId
                : other.IsResource && other.ResourceId.Equals( ResourceId );

            public override bool Equals(object obj) => obj is Node other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return IsResource ? ResourceId.GetHashCode() : TransactionId.GetHashCode() * 397;
                }
            }
        }


        private readonly IDictionary<int, List<int>> edgesOut = new Dictionary<int, List<int>>();
        private readonly IDictionary<Guid, int> transactionToId = new Dictionary<Guid, int>();
        private readonly IDictionary<ParticipantId, int> resourceToId = new Dictionary<ParticipantId, int>();
        private readonly IList<Node> nodes = new List<Node>();
        private bool dirty = true;
        private bool hasCycles;
        private IList<Node> detectedCycle;

        public static WaitForGraph FromLockInfo(IList<LockInfo> lockInfos)
        {
            var wfg = new WaitForGraph();
            foreach (LockInfo lockInfo in lockInfos)
            {
                if(lockInfo.IsWait) wfg.AddWait(lockInfo.TransactionId, lockInfo.ResourceId);
                else wfg.AddLock(lockInfo.TransactionId, lockInfo.ResourceId);
            }

            return wfg;
        }

        private int IdForTransaction(Guid transactionId)
        {
            if (!this.transactionToId.TryGetValue(transactionId, out int id))
            {
                int nextId = this.nodes.Count;
                this.nodes.Add(Node.ForTransaction(transactionId));
                this.transactionToId[transactionId] = id = nextId;
            }

            return id;
        }
        private int IdForResource(ParticipantId resourceId)
        {
            if (!this.resourceToId.TryGetValue(resourceId, out int id))
            {
                int nextId = this.nodes.Count;
                this.nodes.Add(Node.ForResource(resourceId));
                this.resourceToId[resourceId] = id = nextId;
            }

            return id;
        }

        private void AddEdge(int src, int dest)
        {
            this.dirty = true;
            if (!this.edgesOut.TryGetValue(src, out var edges))
            {
                this.edgesOut[src] = edges = new List<int>();
            }

            edges.Add(dest);
        }

        private bool CheckForCycles()
        {
            var colors = new Color[Count];
            var cycle = new List<Node>();
            for (var id = 0; id < this.nodes.Count; id++)
            {
                if (colors[id] == Color.White && DfsHelper(id, colors, cycle))
                {
                    this.hasCycles = true;
                    this.dirty = false;
                    this.detectedCycle = cycle;
                    return true;
                }
                else
                {
                    cycle.Clear();
                }
            }

            this.hasCycles = false;
            this.dirty = false;
            this.detectedCycle = Array.Empty<Node>();
            return false;
        }

        private bool DfsHelper(int rootId, Color[] colors, List<Node> path)
        {
            colors[rootId] = Color.Gray;
            int originalDepth = path.Count;
            path.Add(nodes[rootId]);
            if (this.edgesOut.TryGetValue(rootId, out var edges))
            {
                foreach (var edge in edges)
                {
                    if (colors[edge] == Color.Gray)
                    {
                        path.Add(this.nodes[edge]);
                        return true;
                    }

                    if (colors[edge] == Color.White && DfsHelper(edge, colors, path))
                    {
                        return true;
                    }
                }
            }

            path.RemoveRange(originalDepth, path.Count - originalDepth);
            colors[rootId] = Color.Black;
            return false;
        }

        public int Count => this.nodes.Count;

        public IList<Node> DetectedCycle => this.CheckForCycles() ? detectedCycle : Array.Empty<Node>();

        public void AddWait(Guid transactionId, ParticipantId resourceId)
        {
            int txId = IdForTransaction(transactionId);
            int rsId = IdForResource(resourceId);
            AddEdge(txId, rsId);
        }

        public void AddLock(Guid transactionId, ParticipantId resourceId)
        {
            int txId = IdForTransaction(transactionId);
            int rsId = IdForResource(resourceId);
            AddEdge(rsId, txId);
        }

        public bool HasCycles => this.dirty ? CheckForCycles() : hasCycles;

        public HashSet<LockInfo> LocksToBreak => FindLocksToBreak(DetectedCycle);

        private HashSet<LockInfo> FindLocksToBreak(IList<Node> cycle)
        {
            HashSet<Guid> transactions = new HashSet<Guid>(cycle.Where(n => n.IsTransaction).Select(n => n.TransactionId));
            var result = new HashSet<LockInfo>();
            foreach (Node node in cycle)
            {
                if (!node.IsResource)
                {
                    continue;
                }

                int id = this.resourceToId[node.ResourceId];
                var edges = this.edgesOut[id];
                foreach (int edge in edges)
                {
                    Guid txToAbort = this.nodes[edge].TransactionId;
                    result.Add(new LockInfo
                    {
                        IsWait = false, ResourceId = node.ResourceId, TransactionId = txToAbort
                    });
                }
            }

            return result;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph WFG {");
            foreach (var kv in this.edgesOut)
            {
                var sourceId = kv.Key;
                var destList = kv.Value;
                var source = this.nodes[sourceId];
                foreach (var destId in destList)
                {
                    var dest = this.nodes[destId];
                    if (source.IsTransaction && dest.IsResource)
                    {
                        sb.AppendLine($"\"{source.TransactionId}\" -> \"{dest.ResourceId}\" [label=WAIT];");
                    }
                    else if (source.IsResource && dest.IsTransaction)
                    {
                        sb.AppendLine($"\"{source.ResourceId}\" -> \"{dest.TransactionId}\" [label=LOCK];");
                    }
                    else
                    {
                        throw new Exception($"Invalid edge found: {source.IsResource} {dest.IsResource}");
                    }
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
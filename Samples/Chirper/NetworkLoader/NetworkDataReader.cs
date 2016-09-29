using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Orleans.Runtime;
using Orleans.Samples.Chirper.GrainInterfaces;

namespace Orleans.Samples.Chirper.Network.Loader
{
    /// <summary>
    /// Read a GraphML data file for the Chirper network into memory.
    /// </summary>
    /// <seealso cref="http://graphml.graphdrawing.org/primer/graphml-primer.html">GraphML Primer</seealso>
    /// <seealso cref="http://graphml.graphdrawing.org/specification.html">GraphML Specification</seealso>
    public class NetworkDataReader
    {
        public int ProgressInterval { get; set; }

        public List<XElement> Nodes { get; private set; }
        public List<XElement> Edges { get; private set; }

        private XElement xml;
        private readonly XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
        private string customDataUserAliasValue;

        public NetworkDataReader()
        {
            this.Nodes = new List<XElement>();
            this.Edges = new List<XElement>();
        }

        public void LoadData(FileInfo dataFile)
        {
            this.xml = XElement.Load(dataFile.FullName);
            ParseGraphMLData();
        }

        public List<Task> ProcessNodes(
            Func<ChirperUserInfo, Task> action,
            Action<long> intervalAction = null,
            AsyncPipeline pipeline = null)
        {
            List<Task> promises = new List<Task>();
            long i = 0;
            foreach (XElement nodeData in Nodes)
            {
                ChirperUserInfo userData = ParseUserInfo(nodeData);

                // Call main processing action
                Task n = action(userData);
                if (n != null)
                {
                    if (pipeline != null) pipeline.Add(n);
                    promises.Add(n);
                }
                // else skip this node

                if (intervalAction != null && ProgressInterval != 0)
                {
                    if ((++i % ProgressInterval) == 0)
                    {
                        // Call progress interval action
                        intervalAction(i);
                    }
                }
            }
            return promises;
        }

        public List<Task> ProcessEdges(
            Func<long, long, Task> action,
            Action<long> intervalAction = null,
            AsyncPipeline pipeline = null)
        {
            List<Task> promises = new List<Task>();
            long i = 0;
            foreach (XElement edgeData in Edges)
            {
                long fromUserId = Int64.Parse(edgeData.Attribute("source").Value);
                long toUserId = Int64.Parse(edgeData.Attribute("target").Value);

                Task n = action(fromUserId, toUserId);
                if (n != null)
                {
                    if (pipeline != null) pipeline.Add(n);
                    promises.Add(n);
                }
                else
                {
                    // skip this edge
                    continue;
                }


                if (intervalAction != null && ProgressInterval != 0)
                {
                    if ((++i % ProgressInterval) == 0)
                    {
                        // Call progress interval action
                        intervalAction(i);
                    }
                }
            }
            return promises;
        }

        private void ParseGraphMLData()
        {
            this.Nodes.Clear();
            this.Edges.Clear();

            this.customDataUserAliasValue = xml.Elements(ns + "key").Single(e => e.Attribute("id").Value == "User.Alias").Attribute("attr.name").Value;

            var graph = xml.Elements(ns + "graph").ToArray();
            this.Nodes.AddRange(graph.Elements(ns + "node"));
            this.Edges.AddRange(graph.Elements(ns + "edge"));
        }

        public ChirperUserInfo ParseUserInfo(XElement nodeData)
        {
            var state = ChirperUserInfo.GetUserInfo(
                Int64.Parse(nodeData.Attribute("id").Value),
                nodeData.Elements(ns + "data").Single(e => e.Attribute("key").Value == customDataUserAliasValue).Value);
            return state;
        }
    }
}

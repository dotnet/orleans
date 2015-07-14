﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Xml;

namespace Orleans.Samples.Chirper.Network.Generator
{
    class ChirperGraphMLDocument
    {
        /// <summary>
        /// 
        /// </summary>
        public enum EdgeDefault
        {
            Directed,
            Undirected
        }

        /// <summary>
        /// Holds the set EdgeDefault for easy reference when comparing to the requested value.
        /// </summary>
        private readonly EdgeDefault graphEdgeDefault;

        /// <summary>
        /// The XML namespace for GraphML.
        /// </summary>
        private readonly XNamespace graphmlNamespace = "http://graphml.graphdrawing.org/xmlns";

        /// <summary>
        /// The XmlNamespace for the XMLSchema-instance Schema.
        /// </summary>
        private readonly XNamespace xsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

        /// <summary>
        /// The location of the GraphML Schema.
        /// </summary>
        private const string graphmlSchemaLocation = "http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd";

        /// <summary>
        /// A list for fast lookup of node id's by index.
        /// </summary>
        private readonly SortedSet<KeyValuePair<int, string>> nodeIdLookupList;

        /// <summary>
        /// A sorted list of edge source-target pairs for fast access when writing the edges to the GraphML file.
        /// </summary>
        private readonly SortedSet<Tuple<int, int, int>> edgeSourceSet;

        /// <summary>
        /// The "multiplier" for how many operations to perform before updating the progress display.
        /// </summary>
        const int ProgressInterval = 1000;


        /// <summary>
        /// Constructor builds an empty GraphML document with the given edgedefault.
        /// </summary>
        /// <param name="edgeDefault">The value for the edgedefault required GraphXml attribute. Default is "directed".</param>
        /// <returns>The graphML element from the document.</returns>
        public ChirperGraphMLDocument(EdgeDefault edgeDefault = EdgeDefault.Directed)
        {
            this.graphEdgeDefault = edgeDefault;
            this.nodeIdLookupList = new SortedSet<KeyValuePair<int, string>>(new NodeComparer());
            this.edgeSourceSet = new SortedSet<Tuple<int, int, int>>(new EdgeComparer());
        }

        /// <summary>
        /// Adds an id string to the list of GraphML nodes, if it is not a duplicate.
        /// </summary>
        /// <param name="nodeId">The id to add to the list.</param>
        /// <param name="userId">The id ot the user to add to the list.</param>
        /// <returns>True if successful or false if it couldn't add the id (probably due to the id being a duplicate).</returns>
        public bool AddNode(int nodeId, string userId)
        {
            KeyValuePair<int, string> nodeToken = new KeyValuePair<int, string>(nodeId, userId);
            return this.nodeIdLookupList.Add(nodeToken);
        }

        /// <summary>
        ///     Adds an edge to the list.
        /// </summary>
        /// <param name="id">The id of the edge.</param>
        /// <param name="source">The source node id in a GraphML graph.</param>
        /// <param name="target">The target node id in a GraphML graph.</param>
        /// <returns>True if successful or false if it could not add the edge (probably because it was a duplicate).</returns>
        /// <exception cref="ArgumentException">The target and source values cannot be equal.</exception>
        public bool AddEdge(int id, int source, int target)
        {
            if (source == target)
            {
                throw new ArgumentException("The target and source values cannot be equal.");
            }

            Tuple<int, int, int> edgeToken = new Tuple<int, int, int>(id, source, target);
            return this.edgeSourceSet.Add(edgeToken);
        }
         
        /// <summary>
        /// Writes out the graph in GraphML by sequentially generating Node id's and using the unique edges
        /// generated by a previous call to GenerateUniqueEdges();
        /// </summary>
        /// <param name="graphMLFileName">The name of the file to write the graph to.</param>
        public void WriteXmlWithWriter(string graphMLFileName)
        {
            XmlWriterSettings xmlWriterSettings = new XmlWriterSettings() { 
                Indent = true 
            };
            XmlWriter graphMlWriter = XmlWriter.Create(graphMLFileName, xmlWriterSettings);
            try
            {
                graphMlWriter.WriteStartDocument();

                //<graphml xmlns="http://graphml.graphdrawing.org/xmlns" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd">
                graphMlWriter.WriteStartElement("graphml", graphmlNamespace.NamespaceName);
                graphMlWriter.WriteAttributeString("schemaLocation", xsiNamespace.NamespaceName, graphmlSchemaLocation);

                // <key id="UserAlias" for="node" attr.name="UserAlias" attr.type="string"/>
                const string customDataUserAliasId = "User.Alias";
                const string customDataUserAliasValue = "Alias";
                graphMlWriter.WriteStartElement("key", graphmlNamespace.NamespaceName);
                graphMlWriter.WriteAttributeString("id", customDataUserAliasId);
                graphMlWriter.WriteAttributeString("for", "node");
                graphMlWriter.WriteAttributeString("attr.name", customDataUserAliasValue);
                graphMlWriter.WriteAttributeString("attr.type", "string");
                graphMlWriter.WriteEndElement();

                //<graph edgedefault="directed">
                graphMlWriter.WriteStartElement("graph", graphmlNamespace.NamespaceName);
                graphMlWriter.WriteAttributeString("edgedefault", Enum.GetName(typeof(EdgeDefault), this.graphEdgeDefault));

                Console.Write("\tWriting xml for " + nodeIdLookupList.Count + " nodes ...");

                int nodeCount = 0;
                foreach (KeyValuePair<int, string> nodeData in this.nodeIdLookupList)
                {
                    graphMlWriter.WriteStartElement("node", graphmlNamespace.NamespaceName);
                    graphMlWriter.WriteAttributeString("id", nodeData.Key.ToString(CultureInfo.InvariantCulture));
                    graphMlWriter.WriteStartElement("data", graphmlNamespace.NamespaceName);
                    graphMlWriter.WriteAttributeString("key", "Alias");
                    graphMlWriter.WriteString(nodeData.Value);
                    graphMlWriter.WriteEndElement(); // data
                    graphMlWriter.WriteEndElement(); // node

                    if ((nodeCount++ % ProgressInterval) == 0) Console.Write("."); // Show progress
                }
                Console.WriteLine();

                if (this.edgeSourceSet.Count() > 0)
                {
                    Console.Write("\tWriting xml for " + edgeSourceSet.Count + " edges ...");
                    int edgeId = 0;
                    foreach (Tuple<int, int, int> edgeData in this.edgeSourceSet)
                    {
                        //<edge id="8121" source="4062" target="3270" />
                        graphMlWriter.WriteStartElement("edge", graphmlNamespace.NamespaceName);
                        graphMlWriter.WriteAttributeString("id", edgeData.Item1.ToString(CultureInfo.InvariantCulture));
                        graphMlWriter.WriteAttributeString("source", edgeData.Item2.ToString(CultureInfo.InvariantCulture));
                        graphMlWriter.WriteAttributeString("target", edgeData.Item3.ToString(CultureInfo.InvariantCulture));

                        graphMlWriter.WriteEndElement(); // edge

                        if ((edgeId++ % ProgressInterval) == 0) Console.Write("."); // Show progress
                    }
                }
                Console.WriteLine();

                graphMlWriter.WriteEndElement(); // graph
                graphMlWriter.WriteEndElement(); // graphml (i.e. the root)
                graphMlWriter.WriteEndDocument();
                graphMlWriter.Flush();

            }
            finally
            {
                graphMlWriter.Close();
            }
        }
    }

    class NodeComparer : IComparer<KeyValuePair<int, string>>
    {
        public int Compare(KeyValuePair<int, string> x, KeyValuePair<int, string> y)
        {
            return x.Key - y.Key;
        }
    }

    class EdgeComparer : IComparer<Tuple<int, int, int>>
    {

        public int Compare(Tuple<int, int, int> x, Tuple<int, int, int> y)
        {
            if (x.Item2 != y.Item2)
            {
                return x.Item2 - y.Item2;
            }
            else
            {
                return x.Item3 - y.Item3;
            }

        }
    }

}

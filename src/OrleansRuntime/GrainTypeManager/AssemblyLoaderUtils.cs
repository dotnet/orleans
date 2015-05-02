/*
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
using System.Reflection;

using System.Diagnostics;

namespace Orleans.Runtime
{
    internal class AssemblyLoaderUtils
    {
        private static bool TraceLoadRequests { get; set; }
        private static readonly List<AssemblyLoadLogEntry> assemblyLoadLogEntries = new List<AssemblyLoadLogEntry>();
        private static readonly TraceLogger logger = TraceLogger.GetLogger("AssemblyLoadTracer");

        public static void EnableAssemblyLoadTracing()
        {
            EnableAssemblyLoadTracing(false);
        }

        public static void EnableAssemblyLoadTracing(bool realtimeTrace)
        {
            logger.Info("Starting assembly load request tracing");
            ClearLog();
            TraceLoadRequests = realtimeTrace;

            AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolveTracer;
            AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoadTracer;
        }

        private static void AddLog(AssemblyLoadLogEntry logEntry)
        {
            assemblyLoadLogEntries.Add(logEntry);

            if (TraceLoadRequests)
                DumpLogEntry(logEntry);
        }

        public static void ClearLog()
        {
            assemblyLoadLogEntries.Clear();
        }

        private static void DumpLogEntry(AssemblyLoadLogEntry loadLogEntry)
        {
            var completeDetails = loadLogEntry as AssemblyLoadCompleteDetails;
            if (completeDetails != null)
            {
                logger.Info("Assembly loaded successfully: Assembly={0} from Location={1} {2}",
                   completeDetails.AssemblyName, completeDetails.AssemblyLocation, completeDetails.StackTrace);
            }
            else
            {
                var requestDetails = loadLogEntry as AssemblyLoadRequestDetails;
                logger.Info("Assembly load request: Assembly={0} requested by Assembly={1} {2}",
                   requestDetails.AssemblyName, requestDetails.RequestingAssembly, requestDetails.StackTrace);
            }
        }

        /// <see cref="ResolveEventHandler"/>
        public static Assembly AssemblyResolveTracer(object sender, ResolveEventArgs args)
        {
            var requestDetails = new AssemblyLoadRequestDetails(args);
            AddLog(requestDetails);
            return null;
        }

        /// <see cref="AssemblyLoadEventHandler"/>
        public static void AssemblyLoadTracer(object sender, AssemblyLoadEventArgs args)
        {
            var completeDetails = new AssemblyLoadCompleteDetails(args);
            AddLog(completeDetails);
        }


        /// <see cref="ResolveEventHandler"/>
        private abstract class AssemblyLoadLogEntry
        {
            /// <summary>
            /// The name of the item to resolve.
            /// </summary>
            internal string AssemblyName { get; set; }

            internal StackTrace StackTrace { get; set; }
        }


        private class AssemblyLoadRequestDetails : AssemblyLoadLogEntry
        {
            internal AssemblyLoadRequestDetails(ResolveEventArgs assemblyResolveEvent)
            {
                StackTrace = new StackTrace(3); // Omit event handler callback from the stack trace
                AssemblyName = assemblyResolveEvent.Name;
                RequestingAssembly = assemblyResolveEvent.RequestingAssembly;
            }

            /// <summary>
            /// Gets the assembly whose dependency is being resolved.
            /// </summary>
            internal Assembly RequestingAssembly { get; private set; }
        }

        private class AssemblyLoadCompleteDetails : AssemblyLoadLogEntry
        {
            internal AssemblyLoadCompleteDetails(AssemblyLoadEventArgs assemblyLoadEvent)
            {
                StackTrace = new StackTrace(3); // Omit event handler callback from the stack trace
                var loadedAssembly = assemblyLoadEvent.LoadedAssembly;
                AssemblyName = loadedAssembly.FullName;
                if (loadedAssembly.IsDynamic) return;

                try
                {
                    AssemblyLocation = loadedAssembly.Location;
                }
                catch { }
            }

            /// <summary>
            /// Gets the assembly which has just been loaded from.
            /// </summary>
            internal string AssemblyLocation { get; private set; }
        }
    }
}

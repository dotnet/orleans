using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Manages log sinks
    /// </summary>
    public class CrashUtils
    {
        // TODO: This is a hack (global variable) to work around initialization order issues in telemetry provider code.
        // This is used by Performance Counter code to know which grains to create counters for.
        internal static IList<string> GrainTypes = null;

        private static IntPtr GetProcessHandle(Process process)
        {
            return process.Handle;
        }
    }
}

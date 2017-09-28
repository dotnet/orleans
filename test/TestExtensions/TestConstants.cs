using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using Orleans.Runtime;

namespace TestExtensions
{
    // used for test constants
    internal static class TestConstants
    {
        public static readonly SafeRandom random = new SafeRandom();

        public static readonly TimeSpan InitTimeout =
            Debugger.IsAttached ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1);
        
    }

    public static class TestsUtils
    {
        public static string GetLegacyTraceFileName(string nodeName, DateTime timestamp, string traceFileFolder = null, string traceFilePattern = null)
        {
            const string dateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";
            string traceFileName = null;
            if (traceFilePattern == null
                || string.IsNullOrWhiteSpace(traceFilePattern)
                || traceFilePattern.Equals("false", StringComparison.OrdinalIgnoreCase)
                || traceFilePattern.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                //default trace file pattern
                traceFilePattern = "{0}-{1}.log";
            }

            string traceFileDir = Path.GetDirectoryName(traceFilePattern);
            if (!String.IsNullOrEmpty(traceFileDir) && !Directory.Exists(traceFileDir))
            {
                traceFileName = Path.GetFileName(traceFilePattern);
                string[] alternateDirLocations = { "appdir", "." };
                foreach (var d in alternateDirLocations)
                {
                    if (Directory.Exists(d))
                    {
                        traceFilePattern = Path.Combine(d, traceFileName);
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(traceFileFolder) && !Directory.Exists(traceFileFolder))
            {
                Directory.CreateDirectory(traceFileFolder);
            }

            traceFilePattern = $"{traceFileFolder}\\{traceFilePattern}";
            traceFileName = String.Format(traceFilePattern, nodeName, timestamp.ToUniversalTime().ToString(dateFormat), Dns.GetHostName());

            return traceFileName;
        }
    }
}

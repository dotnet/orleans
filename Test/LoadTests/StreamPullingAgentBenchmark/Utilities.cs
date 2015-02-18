using System;
using System.Diagnostics;
using CommandLine;
using LoadTestBase;
using Orleans.Runtime;
using StreamPullingAgentBenchmark.EmbeddedSiloLoadTest;

namespace StreamPullingAgentBenchmark
{
    public class Utilities
    {
        private static readonly Parser IgnoreUnknownArgumentsParser = new Parser(settings => { settings.IgnoreUnknownArguments = true; });

        public static bool ParseArguments<TOptions>(string[] args, out TOptions options) where TOptions : BaseOptions, new()
        {
            options = new TOptions();
            if (IgnoreUnknownArgumentsParser.ParseArguments(args, options))
            {
                return true;
            }
            else
            {
                LogAlways(string.Format("Failed to parse arguments: {0}.", LoadTestGrainInterfaces.Utils.EnumerableToString(args)));
                LogAlways(string.Format("Usage:\n{0}.", options.GetUsage()));
                return false;
            }
        }

        public static void LogAlways(string msg)
        {
            Trace.WriteLine(msg);
            LoadTestDriverBase.WriteProgress(msg);
        }

        public static void LogIfVerbose(string msg, BaseOptions options)
        {
            Trace.WriteLine(msg);
            if (options.Verbose)
            {
                LoadTestDriverBase.WriteProgress(msg);
            }
        }
    }
}
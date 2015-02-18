using System;
using CommandLine;
using CommandLine.Text;

namespace StreamPullingAgentBenchmark.EmbeddedSiloLoadTest
{
    public class BaseOptions
    {
        [Option("test-name", Required = true, HelpText = "test name to run")]
        public string TestName { get; set; }

        [Option('c', "config", DefaultValue = "ClientConfiguration.xml", HelpText = "path name of client configuration file")]
        public string ClientConfigFile { get; set; }

        [Option('v', "verbose", DefaultValue = true, HelpText = "turn on verbose silo logging")]
        public bool Verbose { get; set; }

        [Option('m', "embed-silos", HelpText = "use a number of embedded silos")]
        public int EmbedSilos { get; set; }
            
        [Option('s', "silo-config", DefaultValue = "OrleansConfiguration.xml", HelpText = "path name of silo configuration file")]
        public string SiloConfigFile { get; set; }

        [Option('d', "deployment-id", HelpText = "specify the deployment id (defaults to none)")]
        public string DeploymentId { get; set; }

        [Option("polling-period", DefaultValue = 30, HelpText = "specify the client's polling period (in seconds)")]
        public int PollingPeriodSeconds { get; set; }

        [Option("warm-up", DefaultValue = 60, HelpText = "specify the length of the warm-up phase (in seconds)")]
        public int WarmUpSeconds { get; set; }

        [Option("test-length", DefaultValue = 600, HelpText = "specify the test length (in seconds)")]
        public int TestLengthSeconds { get; set; }

        // the following options are here because the deployment framework requires them.

        [Option('p', HelpText = "ignored")]
        public int PipelineSize { get; set; }

        [Option('w', HelpText = "ignored")]
        public int NumberOfWorkers { get; set; }

        [Option('t', HelpText = "ignored")]
        public int NumberOfThreads { get; set; }

        [Option("azure", HelpText = "ignored")]
        public bool UseAzureSiloTable { get; set; }

        [Option("testId", HelpText = "ignored")]
        public bool TestId { get; set; }

        [HelpOption]
        public string GetUsage()
        {
            return HelpText.AutoBuild(this);
        }
    }
}

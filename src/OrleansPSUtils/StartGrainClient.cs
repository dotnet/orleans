using Orleans;
using Orleans.Runtime.Configuration;
using System;
using System.IO;
using System.Management.Automation;
using System.Net;

namespace OrleansPSUtils
{
    [Cmdlet(VerbsLifecycle.Start, "GrainClient", DefaultParameterSetName = DefaultSet)]
    public class StartGrainClient : PSCmdlet
    {
        private const string DefaultSet = "Default";
        private const string FilePathSet = "FilePath";
        private const string FileSet = "File";
        private const string ConfigSet = "Config";
        private const string EndpointSet = "Endpoint";

        [Parameter(Position = 1, Mandatory = true, ValueFromPipeline = true, ParameterSetName = FilePathSet)]
        public string ConfigFilePath { get; set; }

        [Parameter(Position = 2, Mandatory = true, ValueFromPipeline = true, ParameterSetName = FileSet)]
        public FileInfo ConfigFile { get; set; }

        [Parameter(Position = 3, Mandatory = true, ValueFromPipeline = true, ParameterSetName = ConfigSet)]
        public ClientConfiguration Config { get; set; }

        [Parameter(Position = 4, Mandatory = true, ValueFromPipeline = true, ParameterSetName = EndpointSet)]
        public IPEndPoint GatewayAddress { get; set; }

        [Parameter(Position = 5, ValueFromPipeline = true, ParameterSetName = EndpointSet)]
        public bool OverrideConfig { get; set; } = true;

        [Parameter(Position = 6, ValueFromPipeline = true, ParameterSetName = FilePathSet)]
        [Parameter(Position = 6, ValueFromPipeline = true, ParameterSetName = FileSet)]
        [Parameter(Position = 6, ValueFromPipeline = true, ParameterSetName = ConfigSet)]
        [Parameter(Position = 6, ValueFromPipeline = true, ParameterSetName = EndpointSet)]
        public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

        protected override void ProcessRecord()
        {
            try
            {
                WriteVerbose($"[{DateTime.UtcNow}] Initializing Orleans Grain Client");

                switch (ParameterSetName)
                {
                    case FilePathSet:
                        WriteVerbose($"[{DateTime.UtcNow}] Using config file at '{ConfigFilePath}'...");
                        if (string.IsNullOrWhiteSpace(ConfigFilePath))
                            throw new ArgumentNullException(nameof(ConfigFilePath));
                        GrainClient.Initialize(ConfigFilePath);
                        break;
                    case FileSet:
                        WriteVerbose($"[{DateTime.UtcNow}] Using provided config file...");
                        if (ConfigFile == null)
                            throw new ArgumentNullException(nameof(ConfigFile));
                        GrainClient.Initialize(ConfigFile);
                        break;
                    case ConfigSet:
                        WriteVerbose($"[{DateTime.UtcNow}] Using provided 'ClientConfiguration' object...");
                        if (Config == null)
                            throw new ArgumentNullException(nameof(Config));
                        GrainClient.Initialize(Config);
                        break;
                    case EndpointSet:
                        WriteVerbose($"[{DateTime.UtcNow}] Using default Orleans Grain Client initializer");
                        if (GatewayAddress == null)
                            throw new ArgumentNullException(nameof(GatewayAddress));
                        GrainClient.Initialize(GatewayAddress, OverrideConfig);
                        break;
                    default:
                        WriteVerbose($"[{DateTime.UtcNow}] Using default Orleans Grain Client initializer");
                        GrainClient.Initialize();
                        break;
                }

                if (Timeout != TimeSpan.Zero)
                    GrainClient.SetResponseTimeout(Timeout);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, ex.GetType().Name, ErrorCategory.InvalidOperation, this));
            }
        }

        protected override void StopProcessing()
        {
            GrainClient.Uninitialize();
        }
    }
}

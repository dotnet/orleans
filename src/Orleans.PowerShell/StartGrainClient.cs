using Orleans;
using Orleans.Runtime.Configuration;
using System;
using System.IO;
using System.Management.Automation;
using System.Net;

namespace OrleansPSUtils
{
    using System.Collections.Generic;

    [Cmdlet(VerbsLifecycle.Start, "GrainClient", DefaultParameterSetName = DefaultSet)]
    public class StartGrainClient : PSCmdlet
    {
        private const string DefaultSet = "Default";
        private const string FilePathSet = "FilePath";
        private const string FileSet = "File";
        private const string ConfigSet = "Config";
        private const string EndpointSet = "Endpoint";

        private IClusterClient client;

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

        protected override void ProcessRecord()
        {
            try
            {
                this.WriteVerbose($"[{DateTime.UtcNow}] Initializing Orleans Grain Client");
                var builder = new ClientBuilder();
                switch (this.ParameterSetName)
                {
                    case FilePathSet:
                        this.WriteVerbose($"[{DateTime.UtcNow}] Using config file at '{this.ConfigFilePath}'...");
                        if (string.IsNullOrWhiteSpace(this.ConfigFilePath))
                            throw new ArgumentNullException(nameof(this.ConfigFilePath));
                        builder.LoadConfiguration(this.ConfigFilePath);
                        break;
                    case FileSet:
                        this.WriteVerbose($"[{DateTime.UtcNow}] Using provided config file...");
                        if (this.ConfigFile == null)
                            throw new ArgumentNullException(nameof(this.ConfigFile));
                        builder.LoadConfiguration(this.ConfigFile);
                        break;
                    case ConfigSet:
                        this.WriteVerbose($"[{DateTime.UtcNow}] Using provided 'ClientConfiguration' object...");
                        if (this.Config == null)
                            throw new ArgumentNullException(nameof(this.Config));
                        builder.UseConfiguration(this.Config);
                        break;
                    case EndpointSet:
                        this.WriteVerbose($"[{DateTime.UtcNow}] Using default Orleans Grain Client initializer");
                        if (this.GatewayAddress == null)
                            throw new ArgumentNullException(nameof(this.GatewayAddress));
                        var config = this.GetOverriddenConfig();
                        builder.UseConfiguration(config);
                        break;
                    default:
                        this.WriteVerbose($"[{DateTime.UtcNow}] Using default Orleans Grain Client initializer");
                        builder.LoadConfiguration();
                        break;
                }

                this.client = builder
                    .ConfigureApplicationParts(parts => parts.ConfigureDefaults())
                    .Build();
                this.client.Connect().GetAwaiter().GetResult();
                this.SetClient(this.client);
                this.WriteObject(this.client);
            }
            catch (Exception ex)
            {
                this.WriteError(new ErrorRecord(ex, ex.GetType().Name, ErrorCategory.InvalidOperation, this));
            }
        }

        private ClientConfiguration GetOverriddenConfig()
        {
            var config = ClientConfiguration.StandardLoad();
            if (config == null)
            {
                Console.WriteLine("Error loading standard client configuration file.");
                throw new ArgumentException("Error loading standard client configuration file");
            }
            if (this.OverrideConfig)
            {
                config.Gateways = new List<IPEndPoint>(new[] {this.GatewayAddress});
            }
            else if (!config.Gateways.Contains(this.GatewayAddress))
            {
                config.Gateways.Add(this.GatewayAddress);
            }
            config.PreferedGatewayIndex = config.Gateways.IndexOf(this.GatewayAddress);
            return config;
        }

        protected override void StopProcessing()
        {
            this.CloseClient(this.client);
        }
    }
}

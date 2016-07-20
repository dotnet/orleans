
using System;
using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;

namespace Tester.TestStreamProviders
{

    /// <summary>
    /// This is a test stream provider that throws exceptions when config file contains certain properties.
    /// </summary>

    public enum FailureInjectionStreamProviderMode
    {
        UnknownException,
        InitializationThrowsException,
        StartThrowsException
    }

    public class FailureInjectionStreamProvider : IStreamProviderImpl
    {
        private IProviderConfiguration config;

        public static string FailureInjectionModeString { get { return "FAILURE_INJECTION_STREAM_PROVIDER_MODE"; } }

        public string Name { get; set; }

        public IAsyncStream<T> GetStream<T>(Guid streamId, string streamNamespace)
        {
            throw new NotImplementedException();
        }

        public bool IsRewindable { get; }

        public Task Close()
        {
            return TaskDone.Done;
        }

        public async Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            Name = name;
            FailureInjectionStreamProviderMode exceptionMode = ProviderConfigurationExtensions.GetEnumProperty<FailureInjectionStreamProviderMode>(config,
                FailureInjectionModeString, FailureInjectionStreamProviderMode.UnknownException);
            if (exceptionMode == FailureInjectionStreamProviderMode.InitializationThrowsException)
            {
                throw new Exception("Error initializing provider "+typeof(FailureInjectionStreamProvider).ToString());
            }
            this.config = config;
        }

        public async Task Start()
        {
            FailureInjectionStreamProviderMode exceptionMode = ProviderConfigurationExtensions.GetEnumProperty<FailureInjectionStreamProviderMode>(config,
                FailureInjectionModeString, FailureInjectionStreamProviderMode.UnknownException);
            if (exceptionMode == FailureInjectionStreamProviderMode.StartThrowsException)
            {
                throw new Exception("Error starting provider " + typeof(FailureInjectionStreamProvider).ToString());
            }
        }
    }
}

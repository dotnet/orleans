using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Async;
using Orleans.Streams;
using Orleans.Providers;

namespace Tester.TestStreamProviders
{
    /// <summary>
    /// This is a test stream provider that throws exceptions when config file contains certain properties.
    /// </summary>
    public enum FailureInjectionStreamProviderMode
    {
        NoFault,
        InitializationThrowsException,
        StartThrowsException
    }

    public class FailureInjectionStreamProvider : IStreamProviderImpl
    {
        private FailureInjectionStreamProviderMode mode;

        public static string FailureInjectionModeString => "FAILURE_INJECTION_STREAM_PROVIDER_MODE";

        public string Name { get; set; }

        public IAsyncStream<T> GetStream<T>(Guid streamId, string streamNamespace)
        {
            throw new NotImplementedException();
        }

        public bool IsRewindable => false;

        public Task Close()
        {
            return TaskDone.Done;
        }

        public Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration providerConfig)
        {
            Name = name;
            mode = providerConfig.GetEnumProperty(FailureInjectionModeString, FailureInjectionStreamProviderMode.NoFault);
            return mode == FailureInjectionStreamProviderMode.InitializationThrowsException
                ? TaskUtility.Faulted(new ProviderInitializationException("Error initializing provider " + typeof(FailureInjectionStreamProvider)))
                : TaskDone.Done;
        }

        public Task Start()
        {
            return mode == FailureInjectionStreamProviderMode.StartThrowsException
                ? TaskUtility.Faulted(new ProviderStartException("Error starting provider " + typeof(FailureInjectionStreamProvider).Name))
                : TaskDone.Done;
        }
    }
}

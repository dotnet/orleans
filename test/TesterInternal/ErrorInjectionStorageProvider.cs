using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;

namespace UnitTests.StorageTests
{
    [Serializable]
    public enum ErrorInjectionPoint
    {
        Unknown = 0,
        None = 1,
        BeforeRead = 2,
        AfterRead = 3,
        BeforeWrite = 4,
        AfterWrite = 5
    }

    [Serializable]
    public class StorageProviderInjectedError : Exception
    {
        private readonly ErrorInjectionPoint errorInjectionPoint;

        public StorageProviderInjectedError(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        public StorageProviderInjectedError(ErrorInjectionPoint errorPoint)
        {
            errorInjectionPoint = errorPoint;
        }

        public StorageProviderInjectedError()
        {
            errorInjectionPoint = ErrorInjectionPoint.Unknown;
        }

        public override string Message
        {
            get
            {
                return "ErrorInjectionPoint=" + Enum.GetName(typeof(ErrorInjectionPoint), errorInjectionPoint);
            }
        }
    }

    public class ErrorInjectionStorageProvider : MockStorageProvider
    {
        public ErrorInjectionPoint ErrorInjection { get; private set; }

        internal static bool DoInjectErrors = true;

        public void SetErrorInjection(ErrorInjectionPoint errorInject)
        {
            ErrorInjection = errorInject;
            Log.Info(0, "Set ErrorInjection to {0}", ErrorInjection);
        }

        public async override Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Log = providerRuntime.GetLogger(this.GetType().FullName);
            Log.Info(0, "Init ErrorInjection={0}", ErrorInjection);
            try
            {
                SetErrorInjection(ErrorInjectionPoint.None);
                await base.Init(name, providerRuntime, config);
            }
            catch (Exception exc)
            {
                Log.Error(0, "Unexpected error during Init", exc);
                throw;
            }
        }

        public async override Task Close()
        {
            Log.Info(0, "Close ErrorInjection={0}", ErrorInjection);
            try
            {
                SetErrorInjection(ErrorInjectionPoint.None);
                await base.Close();
            }
            catch (Exception exc)
            {
                Log.Error(0, "Unexpected error during Close", exc);
                throw;
            }
        }

        public async override Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "ReadStateAsync for {0} {1} ErrorInjection={2}", grainType, grainReference, ErrorInjection);
            try
            {
                if (ErrorInjection == ErrorInjectionPoint.BeforeRead && DoInjectErrors) throw new StorageProviderInjectedError(ErrorInjection);
                await base.ReadStateAsync(grainType, grainReference, grainState);
                if (ErrorInjection == ErrorInjectionPoint.AfterRead && DoInjectErrors) throw new StorageProviderInjectedError(ErrorInjection);
            }
            catch (Exception exc)
            {
                Log.Warn(0, "Injected error during ReadStateAsync for {0} {1} Exception = {2}", grainType, grainReference, exc);
                throw;
            }
        }

        public async override Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "WriteStateAsync for {0} {1} ErrorInjection={0}", grainType, grainReference, ErrorInjection);
            try
            {
                if (ErrorInjection == ErrorInjectionPoint.BeforeWrite && DoInjectErrors) throw new StorageProviderInjectedError(ErrorInjection);
                await base.WriteStateAsync(grainType, grainReference, grainState);
                if (ErrorInjection == ErrorInjectionPoint.AfterWrite && DoInjectErrors) throw new StorageProviderInjectedError(ErrorInjection);
            }
            catch (Exception exc)
            {
                Log.Warn(0, "Injected error during WriteStateAsync for {0} {1} Exception = {2}", grainType, grainReference, exc);
                throw;
            }
        }
    }
}

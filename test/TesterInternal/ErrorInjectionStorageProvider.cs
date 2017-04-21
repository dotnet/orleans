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
    public struct ErrorInjectionBehavior
    {
        public static readonly ErrorInjectionBehavior None = new ErrorInjectionBehavior { ErrorInjectionPoint = ErrorInjectionPoint.None };
        public Type ExceptionType { get; set; }
        public ErrorInjectionPoint ErrorInjectionPoint { get; set; }
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

    public class ErrorInjectionStorageProvider : MockStorageProvider, IControllable
    {
        public static void SetErrorInjection(string providerName, ErrorInjectionBehavior errorInjectionBehavior, IGrainFactory grainFactory)
        {
            IManagementGrain mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);
            mgmtGrain.SendControlCommandToProvider(
                typeof(ErrorInjectionStorageProvider).FullName,
                providerName, 
                (int)Commands.SetErrorInjection,
                errorInjectionBehavior)
                .Wait();
        }

        public ErrorInjectionBehavior ErrorInjection { get; private set; }

        internal static bool DoInjectErrors = true;

        public void SetErrorInjection(ErrorInjectionBehavior errorInject)
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
                SetErrorInjection(ErrorInjectionBehavior.None);
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
                SetErrorInjection(ErrorInjectionBehavior.None);
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
                ThrowIfMatches(ErrorInjectionPoint.BeforeRead);
                await base.ReadStateAsync(grainType, grainReference, grainState);
                ThrowIfMatches(ErrorInjectionPoint.AfterRead);
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
                ThrowIfMatches(ErrorInjectionPoint.BeforeWrite);
                await base.WriteStateAsync(grainType, grainReference, grainState);
                ThrowIfMatches(ErrorInjectionPoint.AfterWrite);
            }
            catch (Exception exc)
            {
                Log.Warn(0, "Injected error during WriteStateAsync for {0} {1} Exception = {2}", grainType, grainReference, exc);
                throw;
            }
        }

        private void ThrowIfMatches(ErrorInjectionPoint executingPoint)
        {
            if (DoInjectErrors && ErrorInjection.ErrorInjectionPoint == executingPoint)
            {
                if (ErrorInjection.ExceptionType == null || ErrorInjection.ExceptionType == typeof(StorageProviderInjectedError))
                {
                    throw new StorageProviderInjectedError(ErrorInjection.ErrorInjectionPoint);
                }
                else
                {
                    throw ((Exception)Activator.CreateInstance(ErrorInjection.ExceptionType));
                }
            }
        }

        #region IControllable interface methods
        /// <summary>
        /// A function to execute a control command.
        /// </summary>
        /// <param name="command">A serial number of the command.</param>
        /// <param name="arg">An opaque command argument</param>
        public override Task<object> ExecuteCommand(int command, object arg)
        { 
            switch ((Commands)command)
            {
                case Commands.SetErrorInjection:
                    SetErrorInjection((ErrorInjectionBehavior)arg);
                    return Task.FromResult<object>(true);
                default:
                    return base.ExecuteCommand(command, arg);
            }
        }
        #endregion
    }
}

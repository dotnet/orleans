using System.Runtime.Serialization;
using Orleans.Providers;
using Orleans.Runtime;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

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
    [GenerateSerializer]
    public struct ErrorInjectionBehavior
    {
        public static readonly ErrorInjectionBehavior None = new ErrorInjectionBehavior { ErrorInjectionPoint = ErrorInjectionPoint.None };

        [Id(0)]
        public Type ExceptionType { get; set; }
        [Id(1)]
        public ErrorInjectionPoint ErrorInjectionPoint { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class StorageProviderInjectedError : OrleansException
    {
        [Id(0)]
        private readonly ErrorInjectionPoint errorInjectionPoint;

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

        protected StorageProviderInjectedError(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        protected StorageProviderInjectedError(string message) : base(message)
        {
        }

        protected StorageProviderInjectedError(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public class ErrorInjectionStorageProvider : MockStorageProvider, IControllable
    {
        private readonly ILogger logger;

        public ErrorInjectionStorageProvider(
            ILogger<ErrorInjectionStorageProvider> logger,
            ILoggerFactory loggerFactory,
            DeepCopier copier) : base(loggerFactory, copier)
        {
            this.logger = logger;
            SetErrorInjection(ErrorInjectionBehavior.None);
        }

        public static async Task SetErrorInjection(string providerName, ErrorInjectionBehavior errorInjectionBehavior, IGrainFactory grainFactory)
        {
            IManagementGrain mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);
            await mgmtGrain.SendControlCommandToProvider(
                typeof(ErrorInjectionStorageProvider).FullName,
                providerName, 
                (int)Commands.SetErrorInjection,
                errorInjectionBehavior);
        }

        public ErrorInjectionBehavior ErrorInjection { get; private set; }

        internal static bool DoInjectErrors = true;

        public void SetErrorInjection(ErrorInjectionBehavior errorInject)
        {
            ErrorInjection = errorInject;
            logger.LogInformation("Set ErrorInjection to {ErrorInjection}", ErrorInjection);
        }
        
        public async override Task Close()
        {
            logger.LogInformation("Close ErrorInjection={ErrorInjection}", ErrorInjection);
            try
            {
                SetErrorInjection(ErrorInjectionBehavior.None);
                await base.Close();
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Unexpected error during Close");
                throw;
            }
        }

        public async override Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            logger.LogInformation( "ReadStateAsync for {GrainType} {GrainId} ErrorInjection={ErrorInjection}", grainType, grainId, ErrorInjection);
            try
            {
                ThrowIfMatches(ErrorInjectionPoint.BeforeRead);
                await base.ReadStateAsync(grainType, grainId, grainState);
                ThrowIfMatches(ErrorInjectionPoint.AfterRead);
            }
            catch (Exception exc)
            {
                logger.LogWarning(exc, "Injected error during ReadStateAsync for {GrainType} {GrainId}", grainType, grainId);
                throw;
            }
        }

        public async override Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            logger.LogInformation("WriteStateAsync for {GrainType} {GrainReference} ErrorInjection={ErrorInjection}", grainType, grainId, ErrorInjection);
            try
            {
                ThrowIfMatches(ErrorInjectionPoint.BeforeWrite);
                await base.WriteStateAsync(grainType, grainId, grainState);
                ThrowIfMatches(ErrorInjectionPoint.AfterWrite);
            }
            catch (Exception exc)
            {
                logger.LogWarning(exc, "Injected error during WriteStateAsync for {GrainType} {GrainId}", grainType, grainId);
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
    }
}

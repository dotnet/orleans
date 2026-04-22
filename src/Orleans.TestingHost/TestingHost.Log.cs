using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    public partial class StorageFaultGrain
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Activate."
        )]
        private static partial void LogInformationActivated(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Added ReadState fault for {GrainId}."
        )]
        private static partial void LogInformationAddedReadStateFault(ILogger logger, GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Added WriteState fault for {GrainId}."
        )]
        private static partial void LogInformationAddedWriteStateFault(ILogger logger, GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Added ClearState fault for {GrainId}."
        )]
        private static partial void LogInformationAddedClearStateFault(ILogger logger, GrainId grainId);
    }

    public partial class FaultInjectionGrainStorage
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Fault injected for ReadState for grain {GrainId} of type {GrainType}"
        )]
        private static partial void LogInformationFaultInjectedForReadState(ILogger logger, GrainId grainId, string grainType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ReadState for grain {GrainId} of type {GrainType}"
        )]
        private static partial void LogInformationReadState(ILogger logger, GrainId grainId, string grainType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Fault injected for WriteState for grain {GrainId} of type {GrainType}"
        )]
        private static partial void LogInformationFaultInjectedForWriteState(ILogger logger, GrainId grainId, string grainType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "WriteState for grain {GrainId} of type {GrainType}"
        )]
        private static partial void LogInformationWriteState(ILogger logger, GrainId grainId, string grainType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Fault injected for ClearState for grain {GrainId} of type {GrainType}"
        )]
        private static partial void LogInformationFaultInjectedForClearState(ILogger logger, GrainId grainId, string grainType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ClearState for grain {GrainId} of type {GrainType}"
        )]
        private static partial void LogInformationClearState(ILogger logger, GrainId grainId, string grainType);
    }
}

namespace Orleans.TestingHost.InMemoryTransport
{
    internal partial class InMemoryTransportConnection
    {
        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Connection id \"{ConnectionId}\" closing because: \"{Message}\""
        )]
        private static partial void LogDebugConnectionClosing(ILogger logger, string connectionId, string? message);
    }
}

using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.TestKit
{
    public partial class TransactionRecoveryTestsRunner
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "{Message}"
        )]
        private static partial void LogInformationMessage(ILogger logger, string message);
    }

    public partial class RemoteCommitService
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Transaction {TransactionId} Passed with data: {Data}"
        )]
        private static partial void LogInformationTransactionPassed(ILogger logger, Guid transactionId, string data);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Transaction {TransactionId} Failed with data: {Data}"
        )]
        private static partial void LogInformationTransactionFailed(ILogger logger, Guid transactionId, string data);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Transaction {TransactionId} Threw with data: {Data}"
        )]
        private static partial void LogInformationTransactionThrew(ILogger logger, Guid transactionId, string data);
    }

    public partial class MultiStateTransactionalGrainBaseClass
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Setting from {Value} to {NewValue}."
        )]
        private static partial void LogInformationSettingValue(ILogger logger, int value, int newValue);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Set to {Value}."
        )]
        private static partial void LogInformationSetValue(ILogger logger, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Adding {NumberToAdd} to value {Value}."
        )]
        private static partial void LogInformationAddingValue(ILogger logger, int numberToAdd, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Value after Adding {NumberToAdd} is {Value}."
        )]
        private static partial void LogInformationValueAfterAdd(ILogger logger, int numberToAdd, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Get {Value}."
        )]
        private static partial void LogInformationGetValue(ILogger logger, int value);
    }

    public partial class ExclusiveLockTransactionTestGrain
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Setting from {Value} to {NewValue}."
        )]
        private static partial void LogInformationSettingValue(ILogger logger, int value, int newValue);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Set to {Value}."
        )]
        private static partial void LogInformationSetValue(ILogger logger, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Adding {NumberToAdd} to value {Value}."
        )]
        private static partial void LogInformationAddingValue(ILogger logger, int numberToAdd, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Value after Adding {NumberToAdd} is {Value}."
        )]
        private static partial void LogInformationValueAfterAdd(ILogger logger, int numberToAdd, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Get {Value}."
        )]
        private static partial void LogInformationGetValue(ILogger logger, int value);
    }

    public partial class SingleStateFaultInjectionTransactionalGrain
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "GrainId {GrainId}"
        )]
        private static partial void LogInformationGrainId(ILogger logger, Guid grainId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Setting value {NewValue}."
        )]
        private static partial void LogInformationSettingValue(ILogger logger, int newValue);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Adding {NumberToAdd} to value {Value}."
        )]
        private static partial void LogInformationAddingValue(ILogger logger, int numberToAdd, int value);
    }

    public partial class SimpleAzureStorageExceptionInjector
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "{Message}"
        )]
        private static partial void LogInformationMessage(ILogger logger, string message);
    }
}

namespace Orleans.Transactions.TestKit.Correctnesss
{
    public partial class MultiStateTransactionalBitArrayGrain
    {
        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "GrainId: {GrainId}."
        )]
        private static partial void LogTraceGrainId(ILogger logger, Guid grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Setting bit {Index} in state {State}. Transaction {CurrentTransactionId}"
        )]
        private static partial void LogTraceSettingBit(ILogger logger, int index, BitArrayState state, object currentTransactionId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Set bit {Index} in state {State}."
        )]
        private static partial void LogTraceSetBit(ILogger logger, int index, BitArrayState state);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Get state {State}."
        )]
        private static partial void LogTraceGetState(ILogger logger, BitArrayState state);
    }
}

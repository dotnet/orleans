namespace Orleans.Journaling;

internal partial class StateMachineStorage
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Error initializing Azure Cosmos DB Client for membership table provider.")]
    private partial void LogErrorInitializingClient(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error deleting Azure Cosmos DB database.")]
    private partial void LogErrorDeletingDatabase(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Appending {Length} bytes to log {LogId}")]
    private static partial void LogAppend(ILogger logger, long length, string logId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading {Length} bytes from log {LogId}")]
    private static partial void LogRead(ILogger logger, long length, string logId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Log {LogId} replaced, written {Length} bytes.")]
    private static partial void LogReplaced(ILogger logger, string logId, long length);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Initialized CosmosLogStorage for log {LogId}. IsCompacted: {IsCompacted}, EntryCount: {EntryCount}")]
    private static partial void LogInitialized(ILogger logger, string logId, bool isCompacted, int entryCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pending compaction found for log {LogId} during initialization. Attempting recovery.")]
    private static partial void LogPendingCompactionFound(ILogger logger, string logId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading compacted log {LogId}")]
    private static partial void LogReadingCompacted(ILogger logger, string logId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entry {EntryId} for log {LogId} not found during read, though expected.")]
    private static partial void LogWarnNotFoundOnRead(ILogger logger, string logId, string entryId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading {EntryCount} log entries for log {LogId}")]
    private static partial void LogReadingEntries(ILogger logger, string logId, int entryCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "De-compacting log {LogId} to append new entry.")]
    private static partial void LogDecompacting(ILogger logger, string logId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted {EntryCount} Entrys for log {LogId}")]
    private static partial void LogDeleted(ILogger logger, string logId, int entryCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error finalizing compaction for log {LogId}. Status: {StatusCode}, Message: {ErrorMessage}")]
    private static partial void LogErrorFinalizingCompaction(ILogger logger, string logId, string statusCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Finalized compaction for log {LogId}")]
    private static partial void LogFinalizedCompaction(ILogger logger, string logId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error creating pending compaction Entry for log {LogId}. Message: {ErrorMessage}")]
    private static partial void LogErrorCreatingPending(ILogger logger, string logId, string errorMessage, Exception ex);
}

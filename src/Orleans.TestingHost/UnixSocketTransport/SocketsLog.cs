#nullable enable
/*

using System;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

internal static partial class SocketsLog
{
    // Reserved: Event ID 3, EventName = ConnectionRead

    [LoggerMessage(6, LogLevel.Debug, @"Connection ""{Connection}"" received FIN.", EventName = "ConnectionReadFin", SkipEnabledCheck = true)]
    private static partial void ConnectionReadFinCore(ILogger logger, string connection);

    public static void ConnectionReadFin(ILogger logger, TcpMessageTransport connection)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionReadFinCore(logger, connection.ToString());
        }
    }

    [LoggerMessage(7, LogLevel.Debug, @"Connection ""{Connection}"" sending FIN because: ""{Reason}""", EventName = "ConnectionWriteFin", SkipEnabledCheck = true)]
    private static partial void ConnectionWriteFinCore(ILogger logger, string connection, string reason);

    public static void ConnectionWriteFin(ILogger logger, TcpMessageTransport connection, string reason)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionWriteFinCore(logger, connection.ToString(), reason);
        }
    }

    // Reserved: Event ID 11, EventName = ConnectionWrite

    // Reserved: Event ID 12, EventName = ConnectionWriteCallback

    [LoggerMessage(14, LogLevel.Debug, @"Connection ""{Connection}"" communication error.", EventName = "ConnectionError", SkipEnabledCheck = true)]
    private static partial void ConnectionErrorCore(ILogger logger, string connection, Exception ex);

    public static void ConnectionError(ILogger logger, TcpMessageTransport connection, Exception ex)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionErrorCore(logger, connection.ToString(), ex);
        }
    }

    [LoggerMessage(19, LogLevel.Debug, @"Connection ""{Connection}"" reset.", EventName = "ConnectionReset", SkipEnabledCheck = true)]
    public static partial void ConnectionReset(ILogger logger, string connection);

    public static void ConnectionReset(ILogger logger, TcpMessageTransport connection)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionReset(logger, connection.ToString());
        }
    }

    [LoggerMessage(4, LogLevel.Debug, @"Connection ""{Connection}"" paused.", EventName = "ConnectionPause", SkipEnabledCheck = true)]
    private static partial void ConnectionPauseCore(ILogger logger, string connection);

    public static void ConnectionPause(ILogger logger, TcpMessageTransport connection)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionPauseCore(logger, connection.ToString());
        }
    }

    [LoggerMessage(5, LogLevel.Debug, @"Connection ""{Connection}"" resumed.", EventName = "ConnectionResume", SkipEnabledCheck = true)]
    private static partial void ConnectionResumeCore(ILogger logger, string connection);

    public static void ConnectionResume(ILogger logger, TcpMessageTransport connection)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionResumeCore(logger, connection.ToString());
        }
    }

    [LoggerMessage(20, LogLevel.Debug, @"Connection ""{Connection}"" error during shutdown.", EventName = "ConnectionError", SkipEnabledCheck = true)]
    private static partial void ConnectionShutdownErrorCore(ILogger logger, string connection, Exception ex);

    public static void ConnectionShutdownError(ILogger logger, TcpMessageTransport connection, Exception ex)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            ConnectionShutdownErrorCore(logger, connection.ToString(), ex);
        }
    }
}
*/

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Orleans.Dashboard.Implementation;

internal class TraceWriter : IAsyncDisposable
{
    private readonly DashboardLogger _traceListener;
    private readonly HttpContext _context;
    private readonly StreamWriter _writer;

    public TraceWriter(DashboardLogger traceListener, HttpContext context)
    {
        _context = context;

        _writer = new StreamWriter(context.Response.Body);

        _traceListener = traceListener;
        _traceListener.Add(Write);
    }

    private void Write(EventId eventId, LogLevel level, string message)
    {
        var task = WriteAsync(eventId, level, message);

        task.Ignore();
    }

    public async Task WriteAsync(string message)
    {
        try
        {
            await _writer.WriteAsync(message);
            await _writer.WriteAsync("\r\n");

            await _writer.FlushAsync();

            await _context.Response.Body.FlushAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task WriteAsync(EventId eventId, LogLevel level, string message)
    {
        try
        {
            await _writer.WriteAsync($"{DateTime.UtcNow.ToString(CultureInfo.InvariantCulture)} {GetLogLevelString(level)}: [{eventId,8}] {message}\r\n");

            await _writer.FlushAsync();

            await _context.Response.Body.FlushAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _traceListener.Remove(Write);

        return _writer.DisposeAsync();
    }

    private static string GetLogLevelString(LogLevel logLevel)
    {
        switch (logLevel)
        {
            case LogLevel.Trace:
                return "trce";
            case LogLevel.Debug:
                return "dbug";
            case LogLevel.Information:
                return "info";
            case LogLevel.Warning:
                return "warn";
            case LogLevel.Error:
                return "fail";
            case LogLevel.Critical:
                return "crit";
            default:
                throw new ArgumentOutOfRangeException(nameof(logLevel));
        }
    }
}

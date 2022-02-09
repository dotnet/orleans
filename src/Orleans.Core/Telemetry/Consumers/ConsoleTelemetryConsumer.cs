using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    public class ConsoleTelemetryConsumer : ITraceTelemetryConsumer, IExceptionTelemetryConsumer
    {
        public void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            ConsoleText.WriteError(TraceParserUtils.PrintProperties(exception.Message, properties), exception);
        }

        public void TrackTrace(string message)
        {
            ConsoleText.WriteLine(message);
        }

        public void TrackTrace(string message, IDictionary<string, string> properties = null)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));
        }

        public void TrackTrace(string message, Severity severity)
        {
            switch (severity)
            {
                case Severity.Error:
                    ConsoleText.WriteError(message);
                    break;
                case Severity.Info:
                    ConsoleText.WriteStatus(message);
                    break;
                case Severity.Verbose:
                case Severity.Verbose2:
                case Severity.Verbose3:
                    ConsoleText.WriteUsage(message);
                    break;
                case Severity.Warning:
                    ConsoleText.WriteWarning(message);
                    break;
                case Severity.Off:
                    return;
                default:
                    TrackTrace(message);
                    break;
            }
        }

        public void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties = null)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));
        }

        public void Flush() { }
        public void Close() { }

        private static class ConsoleText
        {
            public static bool IsConsoleAvailable
            {
                get
                {
                    return Environment.UserInteractive;
                }
            }

            public static void WriteError(string msg)
            {
                WriteLine(ConsoleColor.Red, msg);
            }

            public static void WriteError(string msg, Exception exc)
            {
                var logMsg = 
                    msg 
                    + Environment.NewLine
                    + "Exception = " + exc 
                    + Environment.NewLine;

                WriteLine(ConsoleColor.Red, logMsg);
            }

            public static void WriteWarning(string msg)
            {
                WriteLine(ConsoleColor.Yellow, msg);
            }

            public static void WriteStatus(string msg)
            {
                WriteLine(ConsoleColor.Green, msg);
            }

            public static void WriteStatus(string format, params object[] args)
            {
                WriteStatus(string.Format(format, args));
            }

            public static void WriteUsage(string msg)
            {
                WriteLine(ConsoleColor.Yellow, msg);
            }

            public static void WriteLine(string msg)
            {
                try
                {
                    Console.WriteLine(msg);
                }
                catch (ObjectDisposedException){}
            }

            public static void WriteLine(string format, params object[] args)
            {
                try
                {
                    Console.WriteLine(format, args);
                }
                catch (ObjectDisposedException){}
            }

            private static void WriteLine(ConsoleColor color, string msg)
            {
                bool doResetColor = false;
                try
                {
                    try
                    {
                        Console.ForegroundColor = color;
                        doResetColor = true;
                    }
                    catch (Exception errorIgnored)
                    {
                        Console.WriteLine("Ignoring error from Console.ForegroundColor : " + errorIgnored);
                    }

                    try
                    {
                        Console.WriteLine(msg);
                    }
                    finally
                    {
                        if (doResetColor)
                        {
                            try
                            {
                                Console.ResetColor();
                            }
                            catch (Exception errorIgnored)
                            {
                                Console.WriteLine("Ignoring error from Console.ResetColor : " + errorIgnored);
                            }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Console may have already been disposed, so eating ObjectDisposedException exception.
                }
            }
        }
    }
}

using System;

namespace Orleans.Runtime
{
    internal static class ConsoleText
    {
        public static bool IsConsoleAvailable
        {
            get
            {
#if !NETSTANDARD
                return Environment.UserInteractive;
#else
                return true;
#endif
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

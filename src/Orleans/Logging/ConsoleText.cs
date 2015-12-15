using System;

namespace Orleans.Runtime
{
    internal static class ConsoleText
    {
        public static void WriteError(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        public static void WriteError(string msg, Exception exc)
        {
            String logMsg = 
                msg 
                + Environment.NewLine
                + "Exception = " + exc 
                + Environment.NewLine;

            WriteLine(ConsoleColor.Red, logMsg);
        }

        public static void WriteWarning(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(msg);
            Console.ResetColor();
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
            Console.WriteLine(msg);
        }

        public static void WriteLine(string format, params object[] args)
        {
            Console.WriteLine(format,args);
        }

        private static void WriteLine(ConsoleColor color, string msg)
        {
            bool doResetColor = false;
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
    }
}

/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;

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

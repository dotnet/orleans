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

using System;
using System.Reflection;
using Orleans;

namespace Orleans.Samples.Chirper.Network.Driver
{
    class Program
    {
        static void Main(string[] args)
        {
            var prog = new ChirperNetworkDriver();
            int exitCode = 0;

            try
            {
                // Program ident
                AssemblyName thisProg = Assembly.GetExecutingAssembly().GetName();
                string progTitle = string.Format("{0} v{1}",
                    thisProg.Name,
                    thisProg.Version);
                Console.WriteLine(progTitle);
                Console.Title = progTitle;


                try
                {
                    if (!prog.ParseArguments(args))
                    {
                        prog.PrintUsage();
                        exitCode = -1;
                    }
                    else
                    {
                        exitCode = prog.Run();
                    }
                }
                catch (Exception exc)
                {
                    Console.WriteLine("{0} halting due to error - {1}. Exception:{2}", thisProg.Name, exc.Message, exc);
                    exitCode = 1;
                }

                Console.WriteLine("==> Press any key to exit <==");
                Console.ReadKey();

                try
                {
                    prog.Stop();
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Ignoring shutdown error: " + exc);
                }
            }
            finally
            {
                prog.Dispose();
                Environment.Exit(exitCode);
            }
        }
    }
}

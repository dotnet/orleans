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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace Orleans.Samples.Chirper.Network.Loader
{
    class Program
    {
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);

        static void Main(string[] args)
        {
            var prog = new ChirperNetworkLoader();
            int exitCode;
                
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
                    Task<int> run = prog.Run();

                    // Note: Unfortunately we can't use await in Main method.
                    bool ok = run.Wait(timeout);

                    if (run.IsFaulted)
                    {
                        Console.WriteLine("Error running client program: " + run.Exception);
                        prog.DumpStatus();
                        exitCode = 1;
                    }
                    else if (!ok)
                    {
                        Console.WriteLine("Timeout running client program");
                        prog.DumpStatus();
                        exitCode = 2;
                    }
                    else
                    {
                        exitCode = run.Result;
                    }
                }
            }
            catch (Exception exc)
            {
                prog.ReportError(thisProg.Name + " halting due to error", exc);
                exitCode = 1;
            }

            // if we printed usage, we don't need to display statistics.
            if (exitCode != -1)
            {
                prog.WaitForCompletion();
            }
        }
    }
}

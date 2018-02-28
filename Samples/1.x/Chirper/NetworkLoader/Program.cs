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

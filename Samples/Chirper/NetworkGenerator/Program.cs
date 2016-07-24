using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

namespace Orleans.Samples.Chirper.Network.Generator
{
    class Program
    {
        static void Main(string[] args)
        {
            var prog = new ChirperNetworkGenerator();

            // Program identity
            AssemblyName thisProg = Assembly.GetExecutingAssembly().GetName();
            string progTitle = string.Format("{0} v{1}",
                thisProg.Name,
                thisProg.Version);
            Console.WriteLine(progTitle);
            Console.Title = progTitle;

            int exitCode;
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
                prog.LogMessage(string.Format("{0} halting due to error - {1}", thisProg.Name, exc));
                prog.FlushLog();
                exitCode = 1;
            }

            if (!prog.Automated)
            {
                Console.WriteLine("Press any key to exit");
                Console.ReadKey();
            }

            Environment.Exit(exitCode);
        }
    }
}

using System;
using System.Reflection;
using Orleans.Runtime;

namespace Orleans.Counter.Control
{
    class Program
    {
        static int Main(string[] args)
        {
            var prog = new CounterControl();

            // Program ident
            AssemblyName thisProgram = typeof(Program).GetTypeInfo().Assembly.GetName();
            var progTitle = string.Format("{0} v{1}", thisProgram.Name, thisProgram.Version.ToString());
            ConsoleText.WriteStatus(progTitle);
            Console.Title = progTitle;

            int result;
            if (!prog.ParseArguments(args))
            {
                prog.PrintUsage();
                result = -1;
            }
            else
            {
                result = prog.Run();
            }

            if (prog.PauseAtEnd)
            {
                Console.WriteLine("Press any key to exit");
                Console.ReadKey();
            }

            return result;
        }
    }
}

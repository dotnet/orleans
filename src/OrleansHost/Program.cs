using System;


namespace Orleans.Runtime.Host
{
    class Program
    {
        static void Main(string[] args)
        {
            RuntimeVersion.ProgamIdent();

            var prog = new WindowsServerHost();
            
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
                    if (prog.Debug)
                        DumpCommandLineArgs(args);
                    
                    prog.Init();
                    exitCode = prog.Run();
                }
            }
            catch (Exception exc)
            {
                ConsoleText.WriteError(string.Format("{0} halting due to error - {1}", RuntimeVersion.ProgramName, exc.ToString()));
                exitCode = 1;
            }
            finally
            {
                prog.Dispose();
            }

            Environment.Exit(exitCode);
        }

        private static void DumpCommandLineArgs(string[] args)
        {
            ConsoleText.WriteUsage(string.Format("Environment.CommandLine=[{0}]", Environment.CommandLine));

            if (args == null || args.Length == 0)
            {
                ConsoleText.WriteUsage("Called with no Args");
            }
            else
            {
                ConsoleText.WriteUsage(string.Format("Called with args.Length={0}", args.Length));
                for (int i = 0; i < args.Length; i++)
                    ConsoleText.WriteUsage(string.Format("args[{0}]='{1}'", i, args[i]));
                
                string[] cmdArgs = Environment.GetCommandLineArgs();
                ConsoleText.WriteUsage(string.Format("Called with Environment.CommandLineArgs.Length={0}", cmdArgs.Length));
                for (int i = 0; i < cmdArgs.Length; i++)
                    ConsoleText.WriteUsage(string.Format("Environment.CommandLineArgs[{0}]='{1}'", i, cmdArgs[i]));
            }
        }
    }
}

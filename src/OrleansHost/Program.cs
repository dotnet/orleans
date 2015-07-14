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
            ConsoleText.WriteUsage(string.Format("Environement.CommandLine=[{0}]", Environment.CommandLine));

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
                ConsoleText.WriteUsage(string.Format("Called with Environement.CommandLineArgs.Length={0}", cmdArgs.Length));
                for (int i = 0; i < cmdArgs.Length; i++)
                    ConsoleText.WriteUsage(string.Format("Environement.CommandLineArgs[{0}]='{1}'", i, cmdArgs[i]));
            }
        }
    }
}

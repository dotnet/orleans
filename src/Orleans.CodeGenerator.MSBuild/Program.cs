using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Orleans.CodeGenerator.MSBuild;

namespace Microsoft.Orleans.CodeGenerator.MSBuild
{
    public class Program
    {
        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            Console.WriteLine("Orleans.CodeGenerator - command-line = {0}", string.Join(" ", args));

            try
            {
                if (args.Length < 1 || args[0] != "SourceToSource")
                {
                    PrintUsage();
                    return -1;
                }

                return SourceToSource(args.Skip(1).ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine("-- Code Generation FAILED -- \n{0}", LogFormatter.PrintException(ex));
                return 3;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: SourceToSource ARGUMENT_FILE");
            Console.WriteLine();
            Console.WriteLine("ARGUMENT_FILE is a file containing a list of arguments each on their own line.");
            Console.WriteLine("The following arguments are available:");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("       AssemblyName:NAME    Specify the assembly name of the project being");
            Console.WriteLine("                            processed.");
            Console.WriteLine("       CodeGenOutputFile:PATH");
            Console.WriteLine("                            Specify the output file for the generated code.");
            Console.WriteLine("       Compile:PATH         Specify a file to be processed for generation. This");
            Console.WriteLine("                            argument can be specified multiple times.");
            Console.WriteLine("       DebuggerStepThrough:BOOL");
            Console.WriteLine("                            Whether to add DebuggerStepThroughAttribute to");
            Console.WriteLine("                            generated code.");
            Console.WriteLine("       DefineConstants:CONSTANTS");
            Console.WriteLine("                            Specify a list of constants. This argument can be");
            Console.WriteLine("                            specified multiple times.");
            Console.WriteLine("                            CONSTANTS is a comma delimited list of KEY=VALUE");
            Console.WriteLine("                            pairs.");
            Console.WriteLine("       LogLevel:LOG_LEVEL   Specify the log level used during generation for");
            Console.WriteLine("                            logging output.");
            Console.WriteLine("                            LOG_LEVEL can be one of \"Critical\", \"Debug\", \"Error\",");
            Console.WriteLine("                            \"Information\", \"None\", \"Trace\", \"Warning\".");
            Console.WriteLine("       OutputType:OUTPUT_TYPE");
            Console.WriteLine("                            Specify the project output type.");
            Console.WriteLine("                            OUTPUT_TYPE can be one of \"Exe\", \"Library\"");
            Console.WriteLine("       ProjectGuid:GUID     Specify the MSBuild project Guid.");
            Console.WriteLine("       ProjectPath:PATH     Specify the path to the project.");
            Console.WriteLine("       Reference:PATH       Specify a file to be treated as a source reference.");
            Console.WriteLine("                            This argument can be specified multiple times.");
            Console.WriteLine("       TargetPath:PATH      Specify the path for the primary output file of the");
            Console.WriteLine("                            project to be processed.");
            Console.WriteLine("       WaitForDebugger      Pause until a debugger has attached to the process.");
        }

        private static int SourceToSource(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: <ArgumentsFile>");
                return -2;
            }

            using (new AssemblyResolver())
            {
                var cmd = new CodeGeneratorCommand();
                var argsFile = args[0].Trim('"');
                if (!File.Exists(argsFile)) throw new ArgumentException($"Arguments file \"{argsFile}\" does not exist.");

                var fileArgs = File.ReadAllLines(argsFile);
                foreach (var arg in fileArgs)
                {
                    var parts = arg.Split(new[] {':'}, 2);
                    if (parts.Length > 2) throw new ArgumentException($"Argument \"{arg}\" cannot be parsed.");
                    var key = parts[0];
                    var value = parts.Skip(1).SingleOrDefault();
                    switch (key)
                    {
                        case "WaitForDebugger":
                            var i = 0;
                            while (!Debugger.IsAttached)
                            {
                                if (i++ % 50 == 0)
                                {
                                    Console.WriteLine("Waiting for debugger to attach.");
                                }

                                Thread.Sleep(100);
                            }

                            break;
                        case nameof(cmd.ProjectGuid):
                            cmd.ProjectGuid = value;
                            break;
                        case nameof(cmd.ProjectPath):
                            cmd.ProjectPath = value;
                            break;
                        case nameof(cmd.OutputType):
                            cmd.OutputType = value;
                            break;
                        case nameof(cmd.TargetPath):
                            cmd.TargetPath = value;
                            break;
                        case nameof(cmd.AssemblyName):
                            cmd.AssemblyName = value;
                            break;
                        case nameof(cmd.Compile):
                            cmd.Compile.Add(value);
                            break;
                        case nameof(cmd.Reference):
                            cmd.Reference.Add(value);
                            break;
                        case nameof(cmd.DefineConstants):
                            cmd.DefineConstants.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
                            break;
                        case nameof(cmd.CodeGenOutputFile):
                            cmd.CodeGenOutputFile = value;
                            break;
                        case nameof(cmd.DebuggerStepThrough):
                            cmd.DebuggerStepThrough = bool.Parse(value);
                            break;
                        case "InputHash":
                            break;
                        case "LogLevel":
                            break;
                        default:
                            PrintUsage();
                            throw new ArgumentOutOfRangeException($"Key \"{key}\" in argument file is unknown.");
                    }
                }

                var ok = cmd.Execute(CancellationToken.None).GetAwaiter().GetResult();
                if (ok) return 0;
            }

            return -1;
        }
    }
}

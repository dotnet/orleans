using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            Console.WriteLine("Usage: /in:<grain assembly filename> /out:<fileName for output file> /r:<reference assemblies>");
            Console.WriteLine("       @<arguments fileName> - Arguments will be read and processed from this file.");
            Console.WriteLine();
            Console.WriteLine("Example: /in:MyGrain.dll /out:C:\\OrleansSample\\MyGrain\\obj\\Debug\\MyGrain.orleans.g.cs /r:Orleans.dll;..\\MyInterfaces\\bin\\Debug\\MyInterfaces.dll");
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
                var logLevel = LogLevel.Warning;

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
                        case nameof(LogLevel):
                            if (!Enum.TryParse(ignoreCase: true, value: value, result: out logLevel))
                            {
                                var validValues = string.Join(", ", Enum.GetNames(typeof(LogLevel)).Select(v => v.ToString()));
                                Console.WriteLine($"ERROR: \"{value}\" is not a valid log level. Valid values are {validValues}");
                                return -3;
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException($"Key \"{key}\" in argument file is unknown.");
                    }
                }

                var services = new ServiceCollection()
                    .AddLogging(logging =>
                    {
                        logging
                        .SetMinimumLevel(logLevel)
                        .AddConsole()
                        .AddDebug();
                    })
                    .BuildServiceProvider();
                cmd.Log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Orleans.CodeGenerator");
                var stopwatch = Stopwatch.StartNew();
                var ok = cmd.Execute(CancellationToken.None).GetAwaiter().GetResult();
                cmd.Log.LogInformation($"Total code generation time: {stopwatch.ElapsedMilliseconds}ms.");

                if (ok) return 0;
            }

            return -1;
        }
    }
}

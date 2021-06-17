using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.CodeGenerator.MSBuild
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: <ArgumentsFile>");
                return -2;
            }

            using (new AssemblyResolver())
            using (var loggerFactory = new LoggerFactory())
            {
                var cmd = new CodeGeneratorCommand();
                var logLevel = LogLevel.Warning;

                var argsFile = args[0].Trim('"');
                if (!File.Exists(argsFile))
                {
                    throw new ArgumentException($"Arguments file \"{argsFile}\" does not exist.");
                }

                var fileArgs = File.ReadAllLines(argsFile);
                foreach (var arg in fileArgs)
                {
                    var parts = arg.Split(new[] { ':' }, 2);
                    if (parts.Length > 2)
                    {
                        throw new ArgumentException($"Argument \"{arg}\" cannot be parsed.");
                    }

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
                        case nameof(cmd.Compile):
                            cmd.Compile.Add(value);
                            break;
                        case nameof(cmd.Reference):
                            cmd.Reference.Add(value);
                            break;
                        case nameof(cmd.CodeGenOutputFile):
                            cmd.CodeGenOutputFile = value;
                            break;
                        case nameof(cmd.IdAttributes):
                            cmd.IdAttributes.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList());
                            break;
                        case nameof(cmd.ImmutableAttributes):
                            cmd.ImmutableAttributes.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList());
                            break;
                        case nameof(cmd.GenerateSerializerAttributes):
                            cmd.GenerateSerializerAttributes.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList());
                            break;
                        case nameof(cmd.AliasAttributes):
                            cmd.AliasAttributes.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList());
                            break;
                        case "LogLevel":
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

                var serviceProvider = new ServiceCollection().AddLogging(logging => logging.AddConsole().SetMinimumLevel(logLevel)).BuildServiceProvider();
                
                cmd.Log = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orleans.CodeGenerator");
                var stopwatch = Stopwatch.StartNew();
                var ok = cmd.Execute(CancellationToken.None).GetAwaiter().GetResult();
                cmd.Log.LogInformation($"Code generation completed in {stopwatch.ElapsedMilliseconds}ms.");
                if (ok)
                {
                    return 0;
                }
            }

            return -1;
        }
    }
}

using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Generates factory, grain reference, and invoker classes for grain interfaces.
    /// Generates state object classes for grain implementation classes.
    /// </summary>
    public class GrainClientGenerator : MarshalByRefObject
    {
        public int RunMain(string[] args)
        {
            Console.WriteLine("Orleans-CodeGen - command-line = {0}", Environment.CommandLine);

            if (args.Length < 1)
            {
                PrintUsage();

                return 1;
            }

            try
            {
                var options = new CodeGenOptions();

                // STEP 1 : Parse parameters
                if (args.Length == 1 && args[0].StartsWith("@"))
                {
                    // Read command line args from file
                    string arg = args[0];
                    string argsFile = arg.Trim('"').Substring(1).Trim('"');
                    Console.WriteLine("Orleans-CodeGen - Reading code-gen params from file={0}", argsFile);
                    AssertWellFormed(argsFile);
                    args = File.ReadAllLines(argsFile);
                }
                foreach (string a in args)
                {
                    string arg = a.Trim('"').Trim().Trim('"');
                    if (string.IsNullOrEmpty(arg) || string.IsNullOrWhiteSpace(arg)) continue;

                    if (arg.StartsWith("/"))
                    {
                        if (arg.StartsWith("/reference:") || arg.StartsWith("/r:"))
                        {
                            // list of references passed from from project file. separator =';'
                            string refstr = arg.Substring(arg.IndexOf(':') + 1);
                            string[] references = refstr.Split(';');
                            foreach (string rp in references)
                            {
                                AssertWellFormed(rp);
                                options.ReferencedAssemblies.Add(rp);
                            }
                        }
                        else if (arg.StartsWith("/in:"))
                        {
                            var infile = arg.Substring(arg.IndexOf(':') + 1);
                            AssertWellFormed(infile);
                            options.InputAssembly = new FileInfo(infile);
                        }
                        else if (arg.StartsWith("/out:"))
                        {
                            var outfile = arg.Substring(arg.IndexOf(':') + 1);
                            AssertWellFormed(outfile);
                            options.OutputFileName = outfile;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Invalid argument: {arg}.");

                        PrintUsage();

                        return 1;
                    }
                }

                // STEP 2 : Validate and calculate unspecified parameters
                if (options.InputAssembly == null)
                {
                    Console.WriteLine("ERROR: Orleans-CodeGen - no input file specified.");
                    return 2;
                }

                if (String.IsNullOrEmpty(options.OutputFileName))
                {
                    Console.WriteLine("ERROR: Orleans-Codegen - no output filename specified");
                    return 2;
                }

                // STEP 3 : Dump useful info for debugging
                Console.WriteLine($"Orleans-CodeGen - Options {Environment.NewLine}\tInputLib={options.InputAssembly.FullName}{Environment.NewLine}\tOutputFileName={options.OutputFileName}");

                // STEP 5 : Finally call code generation
                if (!new CodeGenerator(options, Console.WriteLine).GenerateCode()) return -1;

                // DONE!
                return 0;
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

        private static void AssertWellFormed(string path)
        {
            CheckPathNotStartWith(path, ":");
            CheckPathNotStartWith(path, "\"");
            CheckPathNotEndsWith(path, "\"");
            CheckPathNotEndsWith(path, "/");
            CheckPath(path, p => !string.IsNullOrWhiteSpace(p), "Empty path string");
        }
        
        private static void CheckPathNotStartWith(string path, string str)
        {
            CheckPath(path, p => !p.StartsWith(str), string.Format("Cannot start with '{0}'", str));
        }

        private static void CheckPathNotEndsWith(string path, string str)
        {
            CheckPath(
                path,
                p => !p.EndsWith(str, StringComparison.InvariantCultureIgnoreCase),
                string.Format("Cannot end with '{0}'", str));
        }

        private static void CheckPath(string path, Func<string, bool> condition, string what)
        {
            if (condition(path)) return;

            var errMsg = string.Format("Bad path {0} Reason = {1}", path, what);
            Console.WriteLine("CODEGEN-ERROR: " + errMsg);
            throw new ArgumentException("FAILED: " + errMsg);
        }

        private static class LogFormatter
        {
            /// <summary>
            /// Utility function to convert an exception into printable format, including expanding and formatting any nested sub-expressions.
            /// </summary>
            /// <param name="exception">The exception to be printed.</param>
            /// <returns>Formatted string representation of the exception, including expanding and formatting any nested sub-expressions.</returns>
            public static string PrintException(Exception exception)
            {
                return exception == null ? String.Empty : PrintException_Helper(exception, 0, true);
            }

            private static string PrintException_Helper(Exception exception, int level, bool includeStackTrace)
            {
                if (exception == null) return String.Empty;
                var sb = new StringBuilder();
                sb.Append(PrintOneException(exception, level, includeStackTrace));
                if (exception is ReflectionTypeLoadException loadException)
                {
                    var loaderExceptions = loadException.LoaderExceptions;
                    if (loaderExceptions == null || loaderExceptions.Length == 0)
                    {
                        sb.Append("No LoaderExceptions found");
                    }
                    else
                    {
                        foreach (Exception inner in loaderExceptions)
                        {
                            // call recursively on all loader exceptions. Same level for all.
                            sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                        }
                    }
                }
                else if (exception is AggregateException)
                {
                    var innerExceptions = ((AggregateException)exception).InnerExceptions;
                    if (innerExceptions == null) return sb.ToString();

                    foreach (Exception inner in innerExceptions)
                    {
                        // call recursively on all inner exceptions. Same level for all.
                        sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                    }
                }
                else if (exception.InnerException != null)
                {
                    // call recursively on a single inner exception.
                    sb.Append(PrintException_Helper(exception.InnerException, level + 1, includeStackTrace));
                }
                return sb.ToString();
            }

            private static string PrintOneException(Exception exception, int level, bool includeStackTrace)
            {
                if (exception == null) return String.Empty;
                string stack = String.Empty;
                if (includeStackTrace && exception.StackTrace != null)
                    stack = String.Format(Environment.NewLine + exception.StackTrace);

                string message = exception.Message;

                return string.Format(Environment.NewLine + "Exc level {0}: {1}: {2}{3}",
                                     level,
                                     exception.GetType(),
                                     message,
                                     stack);
            }
        }
    }
}

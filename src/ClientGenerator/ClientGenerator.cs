namespace Orleans.CodeGeneration
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using Orleans.CodeGenerator;
    using Orleans.Configuration;
    using Orleans.Logging;
    using Orleans.Runtime;
    using Orleans.Serialization;
    using Orleans.Runtime.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Generates factory, grain reference, and invoker classes for grain interfaces.
    /// Generates state object classes for grain implementation classes.
    /// </summary>
    public class GrainClientGenerator : MarshalByRefObject
    {
        [Serializable]
        internal class CodeGenOptions
        {
            public FileInfo InputAssembly;

            public List<string> ReferencedAssemblies = new List<string>();

            public string OutputFileName;
        }

        [Serializable]
        internal class GrainClientGeneratorFlags
        {
            internal static bool Verbose = false;

            internal static bool FailOnPathNotFound = false;
        }

        private static readonly int[] suppressCompilerWarnings =
        {
            162, // CS0162 - Unreachable code detected.
            219, // CS0219 - The variable 'V' is assigned but its value is never used.
            414, // CS0414 - The private field 'F' is assigned but its value is never used.
            649, // CS0649 - Field 'F' is never assigned to, and will always have its default value.
            693, // CS0693 - Type parameter 'type parameter' has the same name as the type parameter from outer type 'T'
            1591, // CS1591 - Missing XML comment for publicly visible type or member 'Type_or_Member'
            1998 // CS1998 - This async method lacks 'await' operators and will run synchronously
        };
        
        private static readonly string OrleansAssemblyFileName = Path.GetFileName(typeof(RuntimeVersion).Assembly.Location);

        /// <summary>
        /// Generates one GrainReference class for each Grain Type in the inputLib file 
        /// and output code file under outputLib directory
        /// </summary>
        private static bool CreateGrainClientAssembly(CodeGenOptions options)
        {
            string generatedCode = null;
            AppDomain appDomain = null;
            try
            {
                var assembly = typeof (GrainClientGenerator).GetTypeInfo().Assembly;
                // Create AppDomain.
                var appDomainSetup = new AppDomainSetup
                {
                    ApplicationBase = Path.GetDirectoryName(assembly.Location),
                    DisallowBindingRedirects = false,
                    ConfigurationFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile
                };
                appDomain = AppDomain.CreateDomain("Orleans-CodeGen Domain", null, appDomainSetup);

                // Set up assembly resolver
                var refResolver = new ReferenceResolver(options.ReferencedAssemblies);
                appDomain.AssemblyResolve += refResolver.ResolveAssembly;

                // Create an instance 
                var generator =
                    (GrainClientGenerator)
                    appDomain.CreateInstanceAndUnwrap(
                        assembly.FullName,
                        typeof(GrainClientGenerator).FullName);

                // Call a method 
                generatedCode = generator.CreateGrainClient(options);
            }
            finally
            {
                if (appDomain != null) AppDomain.Unload(appDomain); // Unload the AppDomain
            }

            if (generatedCode != null)
            {
                using (var sourceWriter = new StreamWriter(options.OutputFileName))
                {
                    sourceWriter.WriteLine("#if !EXCLUDE_CODEGEN");
                    DisableWarnings(sourceWriter, suppressCompilerWarnings);
                    sourceWriter.WriteLine(generatedCode);
                    RestoreWarnings(sourceWriter, suppressCompilerWarnings);
                    sourceWriter.WriteLine("#endif");
                }

                ConsoleText.WriteStatus("Orleans-CodeGen - Generated file written {0}", options.OutputFileName);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Generate one GrainReference class for each Grain Type in the inputLib file 
        /// and output a string with the code
        /// </summary>
        private string CreateGrainClient(CodeGenOptions options)
        {
            // Load input assembly 
            // special case Orleans.dll because there is a circular dependency.
            var assemblyName = AssemblyName.GetAssemblyName(options.InputAssembly.FullName);
            var grainAssembly = (Path.GetFileName(options.InputAssembly.FullName) != OrleansAssemblyFileName)
                                    ? Assembly.LoadFrom(options.InputAssembly.FullName)
                                    : Assembly.Load(assemblyName);

            // Create directory for output file if it does not exist
            var outputFileDirectory = Path.GetDirectoryName(options.OutputFileName);

            if (!String.IsNullOrEmpty(outputFileDirectory) && !Directory.Exists(outputFileDirectory))
            {
                Directory.CreateDirectory(outputFileDirectory);
            }

            var config = new ClusterConfiguration();
            var serializationProviderOptions = Options.Create(new SerializationProviderOptions
            {
                SerializationProviders = config.Globals.SerializationProviders,
                FallbackSerializationProvider = config.Globals.FallbackSerializationProvider
            });
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new FileLoggerProvider("ClientGenerator.log"));
            var codeGenerator = new RoslynCodeGenerator(new SerializationManager(null, serializationProviderOptions, config.Defaults, loggerFactory), loggerFactory);

            // Generate source
            ConsoleText.WriteStatus("Orleans-CodeGen - Generating file {0}", options.OutputFileName);

            return codeGenerator.GenerateSourceForAssembly(grainAssembly);
        }

        private static void DisableWarnings(TextWriter sourceWriter, IEnumerable<int> warnings)
        {
            foreach (var warningNum in warnings) sourceWriter.WriteLine("#pragma warning disable {0}", warningNum);
        }

        private static void RestoreWarnings(TextWriter sourceWriter, IEnumerable<int> warnings)
        {
            foreach (var warningNum in warnings) sourceWriter.WriteLine("#pragma warning restore {0}", warningNum);
        }

        public int RunMain(string[] args)
        {
            ConsoleText.WriteStatus("Orleans-CodeGen - command-line = {0}", Environment.CommandLine);

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
                    AssertWellFormed(argsFile, true);
                    args = File.ReadAllLines(argsFile);
                }
                int i = 1;
                foreach (string a in args)
                {
                    string arg = a.Trim('"').Trim().Trim('"');
                    if (GrainClientGeneratorFlags.Verbose) Console.WriteLine("Orleans-CodeGen - arg #{0}={1}", i++, arg);
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
                                AssertWellFormed(rp, true);
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
                            AssertWellFormed(outfile, false);
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

                bool referencesOrleans = options.InputAssembly.Name.Equals(OrleansAssemblyFileName);
                if (options.ReferencedAssemblies != null)
                {
                    Console.WriteLine("Orleans-CodeGen - Using referenced libraries:");
                    foreach (string assembly in options.ReferencedAssemblies)
                    {
                        var fileName = Path.GetFileName(assembly);
                        Console.WriteLine("\t{0} => {1}", fileName, assembly);
                        if (fileName != null && fileName.Equals(OrleansAssemblyFileName)) referencesOrleans = true;
                    }
                }

                if (referencesOrleans)
                {
                    // STEP 5 : Finally call code generation
                    if (!CreateGrainClientAssembly(options))
                    {
                        Console.WriteLine("ERROR: Orleans-CodeGen - the input assembly contained no types which required code generation.");
                        return -1;
                    }
                }
                else
                {
                    Console.WriteLine("ERROR: Orleans-CodeGen - the input assembly does not reference Orleans and therefore code can not be generated.");
                    return -2;
                }

                // DONE!
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("-- Code-gen FAILED -- \n{0}", LogFormatter.PrintException(ex));
                return 3;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: ClientGenerator.exe /in:<grain assembly filename> /out:<fileName for output file> /r:<reference assemblies>");
            Console.WriteLine("       ClientGenerator.exe @<arguments fileName> - Arguments will be read and processed from this file.");
            Console.WriteLine();
            Console.WriteLine("Example: ClientGenerator.exe /in:MyGrain.dll /out:C:\\OrleansSample\\MyGrain\\obj\\Debug\\MyGrain.orleans.g.cs /r:Orleans.dll;..\\MyInterfaces\\bin\\Debug\\MyInterfaces.dll");
        }

        private static void AssertWellFormed(string path, bool mustExist = false)
        {
            CheckPathNotStartWith(path, ":");
            CheckPathNotStartWith(path, "\"");
            CheckPathNotEndsWith(path, "\"");
            CheckPathNotEndsWith(path, "/");
            CheckPath(path, p => !string.IsNullOrWhiteSpace(p), "Empty path string");

            bool exists = FileExists(path);

            if (mustExist && GrainClientGeneratorFlags.FailOnPathNotFound) CheckPath(path, p => exists, "Path not exists");
        }

        private static bool FileExists(string path)
        {
            bool exists = File.Exists(path) || Directory.Exists(path);
            if (!exists) Console.WriteLine("MISSING: Path not exists: {0}", path);
            return exists;
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


        /// <summary>
        /// Simple class that loads the reference assemblies upon the AppDomain.AssemblyResolve
        /// </summary>
        [Serializable]
        internal class ReferenceResolver
        {
            /// <summary>
            /// Dictionary : Assembly file name without extension -> full path
            /// </summary>
            private Dictionary<string, string> referenceAssemblyPaths = new Dictionary<string, string>();

            /// <summary>
            /// Needs to be public so can be serialized accross the the app domain.
            /// </summary>
            public Dictionary<string, string> ReferenceAssemblyPaths
            {
                get
                {
                    return referenceAssemblyPaths;
                }
                set
                {
                    referenceAssemblyPaths = value;
                }
            }

            /// <summary>
            /// Inits the resolver
            /// </summary>
            /// <param name="referencedAssemblies">Full paths of referenced assemblies</param>
            public ReferenceResolver(IEnumerable<string> referencedAssemblies)
            {
                if (null == referencedAssemblies) return;

                foreach (var assemblyPath in referencedAssemblies) referenceAssemblyPaths[Path.GetFileNameWithoutExtension(assemblyPath)] = assemblyPath;
            }

            /// <summary>
            /// Handles System.AppDomain.AssemblyResolve event of an System.AppDomain
            /// </summary>
            /// <param name="sender">The source of the event.</param>
            /// <param name="args">The event data.</param>
            /// <returns>The assembly that resolves the type, assembly, or resource; 
            /// or null if theassembly cannot be resolved.
            /// </returns>
            public Assembly ResolveAssembly(object sender, ResolveEventArgs args)
            {
                Assembly assembly = null;
                string path;
                var asmName = new AssemblyName(args.Name);
                if (referenceAssemblyPaths.TryGetValue(asmName.Name, out path)) assembly = Assembly.LoadFrom(path);
                else ConsoleText.WriteStatus("Could not resolve {0}:", asmName.Name);
                return assembly;
            }
        }
    }
}

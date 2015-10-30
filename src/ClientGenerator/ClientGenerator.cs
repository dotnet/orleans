// Project Orleans Cloud Service SDK ver. 1.0
//  
// Copyright (c) .NET Foundation
// 
// All rights reserved.
//  
// MIT License
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace Orleans.CodeGeneration
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;

    using Orleans.CodeGenerator;
    using Orleans.Runtime;

    /// <summary>
    /// Generates factory, grain reference, and invoker classes for grain interfaces.
    /// Generates state object classes for grain implementation classes.
    /// </summary>
    public class GrainClientGenerator : MarshalByRefObject
    {
        [Serializable]
        internal class CodeGenOptions
        {
            public FileInfo InputLib;

            public FileInfo SigningKey;

            public bool LanguageConflict;

            public Language? TargetLanguage;

            public List<string> ReferencedAssemblies = new List<string>();

            public List<string> SourceFiles = new List<string>();

            public List<string> Defines = new List<string>();

            public List<string> Imports = new List<string>();

            public string RootNamespace;

            public string FSharpCompilerPath;

            public string CodeGenFile;

            public string SourcesDir;

            public string WorkingDirectory;

            public string Config;

            // VB-specific options
            public string MyType;

            public string OptionExplicit;

            public string OptionCompare;

            public string OptionStrict;

            public string OptionInfer;
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

        /// <summary>
        /// Generates one GrainReference class for each Grain Type in the inputLib file 
        /// and output one GrainClient.dll under outputLib directory
        /// </summary>
        private static bool CreateGrainClientAssembly(CodeGenOptions options)
        {
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
                return generator.CreateGrainClient(options);
            }
            finally
            {
                if (appDomain != null) AppDomain.Unload(appDomain); // Unload the AppDomain
            }
        }

        /// <summary>
        /// Generate one GrainReference class for each Grain Type in the inputLib file 
        /// and output one GrainClient.dll under outputLib directory
        /// </summary>
        private bool CreateGrainClient(CodeGenOptions options)
        {
            PlacementStrategy.Initialize();

            // Load input assembly 
            ConsoleText.WriteLine("");
            var assemblyName = AssemblyName.GetAssemblyName(options.InputLib.FullName);
            var grainAssembly = (Path.GetFileName(options.InputLib.FullName) != "Orleans.dll")
                                    ? Assembly.LoadFrom(options.InputLib.FullName)
                                    : Assembly.Load(assemblyName);
                // special case Orleans.dll because there is a circular dependency.

            // Create sources directory
            if (!Directory.Exists(options.SourcesDir)) Directory.CreateDirectory(options.SourcesDir);

            // Generate source
            var outputFileName = Path.Combine(
                options.SourcesDir,
                Path.GetFileNameWithoutExtension(options.InputLib.Name) + ".codegen.cs");
            ConsoleText.WriteStatus("Orleans-CodeGen - Generating file {0}", outputFileName);

            var codeGenerator = RoslynCodeGenerator.Instance;
            using (var sourceWriter = new StreamWriter(outputFileName))
            {
                if (options.TargetLanguage != Language.CSharp)
                {
                    var message = "Compile-time code generation is supported for C# only. "
                                  + "Remove code generation from your project in order to use run-time code generation.";
                    ConsoleText.WriteLine("ERROR: " + message);
                    throw new NotSupportedException(message);
                }

                sourceWriter.WriteLine("#if !EXCLUDE_CODEGEN");
                DisableWarnings(sourceWriter, suppressCompilerWarnings);
                sourceWriter.WriteLine(codeGenerator.GenerateSourceForAssembly(grainAssembly));
                RestoreWarnings(sourceWriter, suppressCompilerWarnings);
                sourceWriter.WriteLine("#endif");
            }

            ConsoleText.WriteStatus("Orleans-CodeGen - Generated file written {0}", outputFileName);

            // Copy intermediate file to permanent location, if newer.
            ConsoleText.WriteStatus(
                "Orleans-CodeGen - Updating IntelliSense file {0} -> {1}",
                outputFileName,
                options.CodeGenFile);
            UpdateIntellisenseFile(options.CodeGenFile, outputFileName);

            return true;
        }

        private static void DisableWarnings(TextWriter sourceWriter, IEnumerable<int> warnings)
        {
            foreach (var warningNum in warnings) sourceWriter.WriteLine("#pragma warning disable {0}", warningNum);
        }

        private static void RestoreWarnings(TextWriter sourceWriter, IEnumerable<int> warnings)
        {
            foreach (var warningNum in warnings) sourceWriter.WriteLine("#pragma warning restore {0}", warningNum);
        }

        /// <summary>
        /// Updates the source file in the project if required.
        /// </summary>
        /// <param name="sourceFileToBeUpdated">Path to file to be updated.</param>
        /// <param name="outputFileGenerated">File that was updated.</param>
        private static void UpdateIntellisenseFile(string sourceFileToBeUpdated, string outputFileGenerated)
        {
            if (string.IsNullOrEmpty(sourceFileToBeUpdated)) throw new ArgumentNullException("sourceFileToBeUpdated", "Output file must not be blank");
            if (string.IsNullOrEmpty(outputFileGenerated)) throw new ArgumentNullException("outputFileGenerated", "Generated file must already exist");

            var sourceToUpdateFileInfo = new FileInfo(sourceFileToBeUpdated);
            var generatedFileInfo = new FileInfo(outputFileGenerated);

            if (!generatedFileInfo.Exists) throw new Exception("Generated file must already exist");

            if (File.Exists(sourceFileToBeUpdated))
            {
                bool filesMatch = CheckFilesMatch(generatedFileInfo, sourceToUpdateFileInfo);
                if (filesMatch)
                {
                    ConsoleText.WriteStatus(
                        "Orleans-CodeGen - No changes to the generated file {0}",
                        sourceFileToBeUpdated);
                    return;
                }

                // we come here only if files don't match
                sourceToUpdateFileInfo.Attributes = sourceToUpdateFileInfo.Attributes & (~FileAttributes.ReadOnly);
                    // remove read only attribute
                ConsoleText.WriteStatus(
                    "Orleans-CodeGen - copying file {0} to {1}",
                    outputFileGenerated,
                    sourceFileToBeUpdated);
                File.Copy(outputFileGenerated, sourceFileToBeUpdated, true);
                filesMatch = CheckFilesMatch(generatedFileInfo, sourceToUpdateFileInfo);
                ConsoleText.WriteStatus(
                    "Orleans-CodeGen - After copying file {0} to {1} Matchs={2}",
                    outputFileGenerated,
                    sourceFileToBeUpdated,
                    filesMatch);
            }
            else
            {
                var dir = Path.GetDirectoryName(sourceFileToBeUpdated);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                ConsoleText.WriteStatus(
                    "Orleans-CodeGen - copying file {0} to {1}",
                    outputFileGenerated,
                    sourceFileToBeUpdated);
                File.Copy(outputFileGenerated, sourceFileToBeUpdated, true);
                bool filesMatch = CheckFilesMatch(generatedFileInfo, sourceToUpdateFileInfo);
                ConsoleText.WriteStatus(
                    "Orleans-CodeGen - After copying file {0} to {1} Matchs={2}",
                    outputFileGenerated,
                    sourceFileToBeUpdated,
                    filesMatch);
            }
        }

        private static bool CheckFilesMatch(FileInfo file1, FileInfo file2)
        {
            bool isMatching;
            long len1 = -1;
            long len2 = -1;

            if (file1.Exists) len1 = file1.Length;
            if (file2.Exists) len2 = file2.Length;

            if (len1 <= 0 || len2 <= 0)
            {
                isMatching = false;
            }
            else if (len1 != len2)
            {
                isMatching = false;
            }
            else
            {
                byte[] arr1 = File.ReadAllBytes(file1.FullName);
                byte[] arr2 = File.ReadAllBytes(file2.FullName);

                isMatching = true; // initially assume files match
                for (int i = 0; i < arr1.Length; i++)
                {
                    if (arr1[i] != arr2[i])
                    {
                        isMatching = false; // unless we know they don't match
                        break;
                    }
                }
            }

            if (GrainClientGeneratorFlags.Verbose)
                ConsoleText.WriteStatus(
                    "Orleans-CodeGen - CheckFilesMatch = {0} File1 = {1} Len = {2} File2 = {3} Len = {4}",
                    isMatching,
                    file1,
                    len1,
                    file2,
                    len2);
            return isMatching;
        }

        private static readonly string CodeGenFileRelativePathCSharp = Path.Combine("Properties", "orleans.codegen.cs");

        public int RunMain(string[] args)
        {
            ConsoleText.WriteStatus("Orleans-CodeGen - command-line = {0}", Environment.CommandLine);

            if (args.Length < 1)
            {
                Console.WriteLine(
                    "Usage: ClientGenerator.exe <grain interface dll path> [<client dll path>] [<key file>] [<referenced assemblies>]");
                Console.WriteLine(
                    "       ClientGenerator.exe /server <grain dll path> [<factory dll path>] [<key file>] [<referenced assemblies>]");
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
                        else if (arg.StartsWith("/cwd:"))
                        {
                            options.WorkingDirectory = arg.Substring(arg.IndexOf(':') + 1);
                        }
                        else if (arg.StartsWith("/in:"))
                        {
                            var infile = arg.Substring(arg.IndexOf(':') + 1);
                            AssertWellFormed(infile);
                            options.InputLib = new FileInfo(infile);
                        }
                        else if (arg.StartsWith("/keyfile:") || arg.StartsWith("/key:"))
                        {
                            string keyFile = arg.Substring(arg.IndexOf(':') + 1);
                            if (!string.IsNullOrWhiteSpace(keyFile))
                            {
                                AssertWellFormed(keyFile, true);
                                options.SigningKey = new FileInfo(keyFile);
                            }
                        }
                        else if (arg.StartsWith("/config:"))
                        {
                            options.Config = arg.Substring(arg.IndexOf(':') + 1);
                        }
                        else if (arg.StartsWith("/fsharp:"))
                        {
                            var path = arg.Substring(arg.IndexOf(':') + 1);
                            if (!string.IsNullOrEmpty(path))
                            {
                                Console.WriteLine("F# compiler path = '{0}' ", path);
                                options.FSharpCompilerPath = path;
                            }
                            else
                            {
                                Console.WriteLine("F# compiler path not set.");
                            }
                        }
                        else if (arg.StartsWith("/rootns:") || arg.StartsWith("/rns:"))
                        {
                            options.RootNamespace = arg.Substring(arg.IndexOf(':') + 1);
                        }
                        else if (arg.StartsWith("/bootstrap") || arg.StartsWith("/boot"))
                        {
                            // special case for building circular dependecy in preprocessing: 
                            // Do not build the input assembly, assume that some other build step 
                            options.CodeGenFile = Path.GetFullPath(CodeGenFileRelativePathCSharp);
                            if (GrainClientGeneratorFlags.Verbose)
                                Console.WriteLine(
                                    "Orleans-CodeGen - Set CodeGenFile={0} from bootstrap",
                                    options.CodeGenFile);
                        }
                        else if (arg.StartsWith("/define:") || arg.StartsWith("/d:"))
                        {
                            // #define constants passed from project file. separator =';'
                            var definsStr = arg.Substring(arg.IndexOf(':') + 1);

                            if (!string.IsNullOrWhiteSpace(definsStr))
                            {
                                string[] defines = definsStr.Split(';');
                                foreach (var define in defines) options.Defines.Add(define);
                            }
                        }
                        else if (arg.StartsWith("/imports:") || arg.StartsWith("/i:"))
                        {
                            // Standard VB imports passed from project file. separator =';'
                            string importsStr = arg.Substring(arg.IndexOf(':') + 1);

                            if (!string.IsNullOrWhiteSpace(importsStr))
                            {
                                string[] imports = importsStr.Split(';');
                                foreach (var import in imports) options.Imports.Add(import);
                            }
                        }
                        else if (arg.StartsWith("/Option")) // VB-specific options
                        {
                            if (arg.StartsWith("/OptionExplicit:")) options.OptionExplicit = arg.Substring(arg.IndexOf(':') + 1);
                            else if (arg.StartsWith("/OptionStrict:")) options.OptionStrict = arg.Substring(arg.IndexOf(':') + 1);
                            else if (arg.StartsWith("/OptionInfer:")) options.OptionInfer = arg.Substring(arg.IndexOf(':') + 1);
                            else if (arg.StartsWith("/OptionCompare:")) options.OptionCompare = arg.Substring(arg.IndexOf(':') + 1);
                        }
                        else if (arg.StartsWith("/MyType:")) // VB-specific option
                        {
                            options.MyType = arg.Substring(arg.IndexOf(':') + 1);
                        }
                        else if (arg.StartsWith("/sources:") || arg.StartsWith("/src:"))
                        {
                            // C# sources passed from from project file. separator = ';'
                            //if (GrainClientGeneratorFlags.Verbose)
                            //    Console.WriteLine("Orleans-CodeGen - Unpacking source file list arg={0}", arg);

                            var sourcesStr = arg.Substring(arg.IndexOf(':') + 1);
                            //if (GrainClientGeneratorFlags.Verbose)
                            //    Console.WriteLine("Orleans-CodeGen - Splitting source file list={0}", sourcesStr);

                            string[] sources = sourcesStr.Split(';');
                            foreach (var source in sources)
                                AddSourceFile(
                                    options.SourceFiles,
                                    ref options.LanguageConflict,
                                    ref options.TargetLanguage,
                                    ref options.CodeGenFile,
                                    source);
                        }
                    }
                    else
                    {
                        // files passed in without associated flags , we'll make the best guess.
                        if (arg.ToLowerInvariant().EndsWith(".snk", StringComparison.InvariantCultureIgnoreCase)) options.SigningKey = new FileInfo(arg);
                        else
                            AddSourceFile(
                                options.SourceFiles,
                                ref options.LanguageConflict,
                                ref options.TargetLanguage,
                                ref options.CodeGenFile,
                                arg);
                    }
                }

                if (!options.TargetLanguage.HasValue)
                {
                    Console.WriteLine("ERROR: Unable to determine source code language to use for code generation.");
                    return 2;
                }

                // STEP 2 : Validate and calculate unspecified parameters
                if (options.InputLib == null)
                {
                    Console.WriteLine("ERROR: Orleans-CodeGen - no input file specified.");
                    return 2;
                }

                if (string.IsNullOrEmpty(options.CodeGenFile))
                {
                    Console.WriteLine(
                        "ERROR: No codegen file. Add a file '{0}' to your project",
                        Path.Combine("Properties", "orleans.codegen.cs"));
                    return 2;
                }

                // STEP 3 :  Check timestamps and skip if output is up-to-date wrt to all inputs
/*                if (!bootstrap && IsProjectUpToDate(options.InputLib, options.SourceFiles, options.ReferencedAssemblies)
                    && !Debugger.IsAttached)
                {
                    Console.WriteLine(
                        "Orleans-CodeGen - Skipping because all output files are up-to-date with respect to the input files.");
                    return 0;
                }*/

                options.SourcesDir = Path.Combine(options.InputLib.DirectoryName, "Generated");

                // STEP 4 : Dump useful info for debugging
                Console.WriteLine(
                    "Orleans-CodeGen - Options " + Environment.NewLine + "\tInputLib={0} " + Environment.NewLine
                    + "\tSigningKey={1} " + Environment.NewLine
                    + "\tCodeGenFile={2}",
                    options.InputLib.FullName,
                    options.SigningKey != null ? options.SigningKey.FullName : "",
                    options.CodeGenFile);

                if (options.ReferencedAssemblies != null)
                {
                    Console.WriteLine("Orleans-CodeGen - Using referenced libraries:");
                    foreach (string assembly in options.ReferencedAssemblies) Console.WriteLine("\t{0} => {1}", Path.GetFileName(assembly), assembly);
                }

                // STEP 5 :
//                if (!bootstrap) BuildInputAssembly(options);

                // STEP 6 : Finally call code generation
                if (!CreateGrainClientAssembly(options)) return -1;

                // DONE!
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("-- Code-gen FAILED -- \n{0}", TraceLogger.PrintException(ex));
                return 3;
            }
        }

        private static void SetLanguageIfMatchNoConflict(
            string arg,
            string extension,
            Language value,
            ref Language? language,
            ref bool conflict)
        {
            if (conflict) return;

            if (arg.EndsWith(extension, StringComparison.InvariantCultureIgnoreCase))
            {
                if (language.HasValue && language != value)
                {
                    language = null;
                    conflict = true;
                }
                else
                {
                    language = value;
                }
            }
        }

        private static void AddSourceFile(
            List<string> sourceFiles,
            ref bool conflict,
            ref Language? language,
            ref string codeGenFile,
            string arg)
        {
            AssertWellFormed(arg, true);
            sourceFiles.Add(arg);

            SetLanguageIfMatchNoConflict(arg, ".cs", Language.CSharp, ref language, ref conflict);
            SetLanguageIfMatchNoConflict(arg, ".vb", Language.VisualBasic, ref language, ref conflict);
            SetLanguageIfMatchNoConflict(arg, ".fs", Language.FSharp, ref language, ref conflict);

            if (conflict || !language.HasValue) return;

            if (GrainClientGeneratorFlags.Verbose) Console.WriteLine("Orleans-CodeGen - Added source file={0}", arg);

            var path = CodeGenFileRelativePathCSharp;

            if (arg.EndsWith(path, StringComparison.InvariantCultureIgnoreCase))
            {
                codeGenFile = Path.GetFullPath(path);
                if (GrainClientGeneratorFlags.Verbose) Console.WriteLine("Orleans-CodeGen - Set CodeGenFile={0} from {1}", codeGenFile, arg);
            }
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

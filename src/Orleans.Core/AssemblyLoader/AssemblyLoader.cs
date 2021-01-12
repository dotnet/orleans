using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class AssemblyLoader
    {
        private readonly Dictionary<string, SearchOption> dirEnumArgs;
        private readonly HashSet<AssemblyLoaderPathNameCriterion> pathNameCriteria;
        private readonly HashSet<AssemblyLoaderReflectionCriterion> reflectionCriteria;
        private readonly ILogger logger;

        internal bool SimulateExcludeCriteriaFailure { get; set; }
        internal bool SimulateLoadCriteriaFailure { get; set; }
        internal bool SimulateReflectionOnlyLoadFailure { get; set; }
        internal bool RethrowDiscoveryExceptions { get; set; }

        private AssemblyLoader(
                Dictionary<string, SearchOption> dirEnumArgs,
                HashSet<AssemblyLoaderPathNameCriterion> pathNameCriteria,
                HashSet<AssemblyLoaderReflectionCriterion> reflectionCriteria,
                ILogger logger)
        {
            this.dirEnumArgs = dirEnumArgs;
            this.pathNameCriteria = pathNameCriteria;
            this.reflectionCriteria = reflectionCriteria;
            this.logger = logger;
            SimulateExcludeCriteriaFailure = false;
            SimulateLoadCriteriaFailure = false;
            SimulateReflectionOnlyLoadFailure = false;
            RethrowDiscoveryExceptions = false;
        }
        
        /// <summary>
        /// Loads assemblies according to caller-defined criteria.
        /// </summary>
        /// <param name="dirEnumArgs">A list of arguments that are passed to Directory.EnumerateFiles(). 
        ///     The sum of the DLLs found from these searches is used as a base set of assemblies for
        ///     criteria to evaluate.</param>
        /// <param name="pathNameCriteria">A list of criteria that are used to disqualify
        ///     assemblies from being loaded based on path name alone (e.g.
        ///     AssemblyLoaderCriteria.ExcludeFileNames) </param>
        /// <param name="reflectionCriteria">A list of criteria that are used to identify
        ///     assemblies to be loaded based on examination of their ReflectionOnly type
        ///     information (e.g. AssemblyLoaderCriteria.LoadTypesAssignableFrom).</param>
        /// <param name="logger">A logger to provide feedback to.</param>
        /// <returns>List of discovered assemblies</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Reflection.Assembly.LoadFrom")]
        public static List<Assembly> LoadAssemblies(
                Dictionary<string, SearchOption> dirEnumArgs,
                IEnumerable<AssemblyLoaderPathNameCriterion> pathNameCriteria,
                IEnumerable<AssemblyLoaderReflectionCriterion> reflectionCriteria,
                ILogger logger)
        {
            var loader =
                NewAssemblyLoader(
                    dirEnumArgs,
                    pathNameCriteria,
                    reflectionCriteria,
                    logger);
            
            var loadedAssemblies = new List<Assembly>();
            List<string> discoveredAssemblyLocations = loader.DiscoverAssemblies();
            foreach (var pathName in discoveredAssemblyLocations)
            {
                loader.logger.Info("Loading assembly {0}...", pathName);

                // It is okay to use LoadFrom here because we are loading application assemblies deployed to the specific directory.
                // Such application assemblies should not be deployed somewhere else, e.g. GAC, so this is safe.
                try
                {
                    loadedAssemblies.Add(loader.LoadAssemblyFromProbingPath(pathName));
                }
                catch (Exception exception)
                {
                    loader.logger.Warn(ErrorCode.Loader_AssemblyLoadError, $"Failed to load assembly {pathName}.", exception);
                }
            }

            loader.logger.Info("{0} assemblies loaded.", loadedAssemblies.Count);
            return loadedAssemblies;
        }

        public static T LoadAndCreateInstance<T>(string assemblyName, ILogger logger, IServiceProvider serviceProvider) where T : class
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                var foundType = TypeUtils.GetTypes(assembly, type => typeof(T).IsAssignableFrom(type), logger).First();

                return (T)ActivatorUtilities.GetServiceOrCreateInstance(serviceProvider, foundType);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Loader_LoadAndCreateInstance_Failure, exc.Message, exc);
                throw;
            }
        }

        // this method is internal so that it can be accessed from unit tests, which only test the discovery
        // process-- not the actual loading of assemblies.
        internal static AssemblyLoader NewAssemblyLoader(
                Dictionary<string, SearchOption> dirEnumArgs,
                IEnumerable<AssemblyLoaderPathNameCriterion> pathNameCriteria,
                IEnumerable<AssemblyLoaderReflectionCriterion> reflectionCriteria,
                ILogger logger)
        {
            if (null == dirEnumArgs)
                throw new ArgumentNullException("dirEnumArgs");
            if (dirEnumArgs.Count == 0)
                throw new ArgumentException("At least one directory is necessary in order to search for assemblies.");
            HashSet<AssemblyLoaderPathNameCriterion> pathNameCriteriaSet = null == pathNameCriteria 
                ? new HashSet<AssemblyLoaderPathNameCriterion>() 
                : new HashSet<AssemblyLoaderPathNameCriterion>(pathNameCriteria.Distinct());

            if (null == reflectionCriteria || !reflectionCriteria.Any())
                throw new ArgumentException("No assemblies will be loaded unless reflection criteria are specified.");

            var reflectionCriteriaSet = new HashSet<AssemblyLoaderReflectionCriterion>(reflectionCriteria.Distinct());
            if (null == logger)
                throw new ArgumentNullException("logger");

            return new AssemblyLoader(
                    dirEnumArgs,
                    pathNameCriteriaSet,
                    reflectionCriteriaSet,
                    logger);
        }

        // this method is internal so that it can be accessed from unit tests, which only test the discovery
        // process-- not the actual loading of assemblies.
        internal List<string> DiscoverAssemblies()
        {
            try
            {
                if (dirEnumArgs.Count == 0)
                    throw new InvalidOperationException("Please specify a directory to search using the AddDirectory or AddRoot methods.");

                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CachedReflectionOnlyTypeResolver.OnReflectionOnlyAssemblyResolve;
                // the following explicit loop ensures that the finally clause is invoked
                // after we're done enumerating.
                return EnumerateApprovedAssemblies();
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= CachedReflectionOnlyTypeResolver.OnReflectionOnlyAssemblyResolve;
            }
        }

        private List<string> EnumerateApprovedAssemblies()
        {
            var assemblies = new List<string>();
            foreach (var i in dirEnumArgs)
            {
                var pathName = i.Key;
                var searchOption = i.Value;

                if (!Directory.Exists(pathName))
                {
                    logger.Warn(ErrorCode.Loader_DirNotFound, "Unable to find directory {0}; skipping.", pathName);
                    continue;
                }

                logger.Info(
                    searchOption == SearchOption.TopDirectoryOnly ? 
                        "Searching for assemblies in {0}..." :
                        "Recursively searching for assemblies in {0}...",
                    pathName);

                var candidates = 
                    Directory.EnumerateFiles(pathName, "*.dll", searchOption)
                    .Select(Path.GetFullPath)
                    .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToArray();

                // This is a workaround for the behavior of ReflectionOnlyLoad/ReflectionOnlyLoadFrom
                // that appear not to automatically resolve dependencies.
                // We are trying to pre-load all dlls we find in the folder, so that if one of these
                // assemblies happens to be a dependency of an assembly we later on call 
                // Assembly.DefinedTypes on, the dependency will be already loaded and will get
                // automatically resolved. Ugly, but seems to solve the problem.

                foreach (var j in candidates)
                {
                    try
                    {
                        var complaints = default(string[]);

                        if (IsCompatibleWithCurrentProcess(j, out complaints))
                        {
                            TryReflectionOnlyLoadFromOrFallback(j);
                        }
                        else
                        {
                            if (logger.IsEnabled(LogLevel.Information)) logger.Info("{0} is not compatible with current process, loading is skipped.", j);
                        }
                    }
                    catch (Exception)
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Failed to pre-load assembly {0} in reflection-only context.", j);
                    }
                }

                foreach (var j in candidates)
                {
                    if (AssemblyPassesLoadCriteria(j))
                        assemblies.Add(j);
                }
            }

            return assemblies;
        }

        private Assembly TryReflectionOnlyLoadFromOrFallback(string assembly)
        {
            if (TypeUtils.CanUseReflectionOnly)
            {
                return Assembly.ReflectionOnlyLoadFrom(assembly);
            }

            return this.LoadAssemblyFromProbingPath(assembly);
        }

        private bool ShouldExcludeAssembly(string pathName)
        {
            foreach (var criterion in pathNameCriteria)
            {
                IEnumerable<string> complaints;
                bool shouldExclude;
                try
                {
                    shouldExclude = !criterion.EvaluateCandidate(pathName, out complaints);
                }
                catch (Exception ex)
                {
                    complaints = ReportUnexpectedException(ex);
                    if (RethrowDiscoveryExceptions)
                        throw;
                    
                    shouldExclude = true;
                }

                if (shouldExclude)
                {
                    LogComplaints(pathName, complaints);
                    return true;
                }
            }
            return false;
        }

        private static Assembly MatchWithLoadedAssembly(AssemblyName searchFor, IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies)
            {
                var searchForFullName = searchFor.FullName;
                var candidateFullName = assembly.FullName;
                if (String.Equals(candidateFullName, searchForFullName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;                    
                }
            }
            return null;
        }

        private static Assembly MatchWithLoadedAssembly(AssemblyName searchFor, AppDomain appDomain)
        {
            return 
                MatchWithLoadedAssembly(searchFor, appDomain.GetAssemblies()) ??
                MatchWithLoadedAssembly(searchFor, appDomain.ReflectionOnlyGetAssemblies());
        }

        private static Assembly MatchWithLoadedAssembly(AssemblyName searchFor)
        {
            return MatchWithLoadedAssembly(searchFor, AppDomain.CurrentDomain);
        }

        private static bool InterpretFileLoadException(string asmPathName, out string[] complaints)
        {
            Assembly matched;
            try
            {
                matched = MatchWithLoadedAssembly(AssemblyName.GetAssemblyName(asmPathName));
            }
            catch (BadImageFormatException)
            {
                // this can happen when System.Reflection.Metadata or System.Collections.Immutable assembly version is different (one requires the other) and there is no correct binding redirect in the app.config
                complaints = null;
                return false;
            }

            if (null == matched)
            {
                // something unexpected has occurred. rethrow until we know what we're catching.
                complaints = null;
                return false;
            }
            if (matched.Location != asmPathName)
            {
                complaints = new string[] {String.Format("A conflicting assembly has already been loaded from {0}.", matched.Location)};
                // exception was anticipated.
                return true;
            }
            // we've been asked to not log this because it's not indicative of a problem.
            complaints = null;
            //complaints = new string[] {"Assembly has already been loaded into current application domain."};
            // exception was anticipated.
            return true;
        }

        private string[] ReportUnexpectedException(Exception exception)
        {
            const string msg = "An unexpected exception occurred while attempting to load an assembly.";
            logger.Error(ErrorCode.Loader_UnexpectedException, msg, exception);
            return new string[] {msg};
        }

        private bool ReflectionOnlyLoadAssembly(string pathName, out Assembly assembly, out string[] complaints)
        {
            try
            {
                if (SimulateReflectionOnlyLoadFailure)
                    throw NewTestUnexpectedException();

                if (IsCompatibleWithCurrentProcess(pathName, out complaints))
                {
                    assembly = TryReflectionOnlyLoadFromOrFallback(pathName);
                }
                else
                {
                    assembly = null;
                    return false;
                }
            }
            catch (FileLoadException ex)
            {
                assembly = null;
                if (!InterpretFileLoadException(pathName, out complaints))
                    complaints = ReportUnexpectedException(ex);

                if (RethrowDiscoveryExceptions)
                    throw;
                
                return false;
            }
            catch (Exception ex)
            {
                assembly = null;
                complaints = ReportUnexpectedException(ex);

                if (RethrowDiscoveryExceptions)
                    throw;
                
                return false;
            }

            complaints = null;
            return true;
        }

        private static bool IsCompatibleWithCurrentProcess(string fileName, out string[] complaints)
        {
            complaints = null;
            Stream peImage = null;

            try
            {
                peImage = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var peReader = new PEReader(peImage, PEStreamOptions.PrefetchMetadata))
                {
                    peImage = null;
                    if (peReader.HasMetadata)
                    {
                        var processorArchitecture = ProcessorArchitecture.MSIL;

                        var isPureIL = (peReader.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) != 0;

                        if (peReader.PEHeaders.PEHeader.Magic == PEMagic.PE32Plus)
                            processorArchitecture = ProcessorArchitecture.Amd64;
                        else if ((peReader.PEHeaders.CorHeader.Flags & CorFlags.Requires32Bit) != 0 || !isPureIL)
                            processorArchitecture = ProcessorArchitecture.X86;

                        var isLoadable = (isPureIL && processorArchitecture == ProcessorArchitecture.MSIL) ||
                                             (Environment.Is64BitProcess && processorArchitecture == ProcessorArchitecture.Amd64) ||
                                             (!Environment.Is64BitProcess && processorArchitecture == ProcessorArchitecture.X86);

                        if (!isLoadable)
                        {
                            complaints = new[] { $"The file {fileName} is not loadable into this process, either it is not an MSIL assembly or the complied for a different processor architecture." };
                        }

                        return isLoadable;
                    }
                    else
                    {
                        complaints = new[] { $"The file {fileName} does not contain any CLR metadata, probably it is a native file." };
                        return false;
                    }
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (MissingMethodException)
            {
                complaints = new[] { "MissingMethodException occurred. Please try to add a BindingRedirect for System.Collections.ImmutableCollections to the App.config file to correct this error." };
                return false;
            }
            catch (Exception ex)
            {
                complaints = new[] { LogFormatter.PrintException(ex) };
                return false;
            }
            finally
            {
                peImage?.Dispose();
            }
        }

        private void LogComplaint(string pathName, string complaint)
        {
            LogComplaints(pathName, new string[] { complaint });
        }

        private void LogComplaints(string pathName, IEnumerable<string> complaints)
        {
            var distinctComplaints = complaints.Distinct();
            // generate feedback so that the operator can determine why her DLL didn't load.
            var msg = new StringBuilder();
            string bullet = Environment.NewLine + "\t* ";
            msg.Append($"User assembly ignored: {pathName}");
            int count = 0;
            foreach (var i in distinctComplaints)
            {
                msg.Append(bullet);
                msg.Append(i);
                ++count;
            }

            if (0 == count)
                throw new InvalidOperationException("No complaint provided for assembly.");
            // we can't use an error code here because we want each log message to be displayed.
            logger.Info(msg.ToString());
        }

        private static AggregateException NewTestUnexpectedException()
        {
            var inner = new Exception[] { new OrleansException("Inner Exception #1"), new OrleansException("Inner Exception #2") }; 
            return new AggregateException("Unexpected AssemblyLoader Exception Used for Unit Tests", inner);
        }

        private bool ShouldLoadAssembly(string pathName)
        {
            Assembly assembly;
            string[] loadComplaints;
            if (!ReflectionOnlyLoadAssembly(pathName, out assembly, out loadComplaints))
            {
                if (loadComplaints == null || loadComplaints.Length == 0)
                    return false;
                
                LogComplaints(pathName, loadComplaints);
                return false;
            }
            if (assembly.IsDynamic)
            {
                LogComplaint(pathName, "Assembly is dynamic (not supported).");
                return false;
            }

            var criteriaComplaints = new List<string>();
            foreach (var i in reflectionCriteria)
            {
                IEnumerable<string> complaints;
                try
                {
                    if (SimulateLoadCriteriaFailure)
                        throw NewTestUnexpectedException();
                    
                    if (i.EvaluateCandidate(assembly, out complaints))
                        return true;
                }
                catch (Exception ex)
                {
                    complaints = ReportUnexpectedException(ex);
                    if (RethrowDiscoveryExceptions)
                        throw;
                }
                criteriaComplaints.AddRange(complaints);
            }

            LogComplaints(pathName, criteriaComplaints);
            return false;
        }

        private bool AssemblyPassesLoadCriteria(string pathName)
        {
            return !ShouldExcludeAssembly(pathName) && ShouldLoadAssembly(pathName);
        }

        private Assembly LoadAssemblyFromProbingPath(string path)
        {
            var assemblyName = GetAssemblyNameFromMetadata(path);
            return Assembly.Load(assemblyName);
        }

        private static AssemblyName GetAssemblyNameFromMetadata(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var peFile = new PEReader(stream))
            {
                var reader = peFile.GetMetadataReader();
                var definition = reader.GetAssemblyDefinition();
                var name = reader.GetString(definition.Name);
                return new AssemblyName(name);
            }
        }
    }
}

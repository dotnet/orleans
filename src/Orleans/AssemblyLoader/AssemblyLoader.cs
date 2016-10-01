using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Orleans.Runtime
{
    internal class AssemblyLoader
    {
#if !NETSTANDARD_TODO
        private readonly Dictionary<string, SearchOption> dirEnumArgs;
        private readonly HashSet<AssemblyLoaderPathNameCriterion> pathNameCriteria;
        private readonly HashSet<AssemblyLoaderReflectionCriterion> reflectionCriteria;
        private readonly Logger logger;
        internal bool SimulateExcludeCriteriaFailure { get; set; }
        internal bool SimulateLoadCriteriaFailure { get; set; }
        internal bool SimulateReflectionOnlyLoadFailure { get; set; }
        internal bool RethrowDiscoveryExceptions { get; set; }

        private AssemblyLoader(
                Dictionary<string, SearchOption> dirEnumArgs,
                HashSet<AssemblyLoaderPathNameCriterion> pathNameCriteria,
                HashSet<AssemblyLoaderReflectionCriterion> reflectionCriteria,
                Logger logger)
        {
            this.dirEnumArgs = dirEnumArgs;
            this.pathNameCriteria = pathNameCriteria;
            this.reflectionCriteria = reflectionCriteria;
            this.logger = logger;
            SimulateExcludeCriteriaFailure = false;
            SimulateLoadCriteriaFailure = false;
            SimulateReflectionOnlyLoadFailure = false;
            RethrowDiscoveryExceptions = false;

            // Ensure that each assembly which is loaded is processed.
            AssemblyProcessor.Initialize();
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
        /// <returns>List of discovered assembly locations</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Reflection.Assembly.LoadFrom")]
        public static List<string> LoadAssemblies(
                Dictionary<string, SearchOption> dirEnumArgs,
                IEnumerable<AssemblyLoaderPathNameCriterion> pathNameCriteria,
                IEnumerable<AssemblyLoaderReflectionCriterion> reflectionCriteria,
                Logger logger)
        {
            var loader =
                NewAssemblyLoader(
                    dirEnumArgs,
                    pathNameCriteria,
                    reflectionCriteria,
                    logger);

            int count = 0;
            List<string> discoveredAssemblyLocations = loader.DiscoverAssemblies();
            foreach (var pathName in discoveredAssemblyLocations)
            {
                loader.logger.Info("Loading assembly {0}...", pathName);
                // It is okay to use LoadFrom here because we are loading application assemblies deployed to the specific directory.
                // Such application assemblies should not be deployed somewhere else, e.g. GAC, so this is safe.
                Assembly.LoadFrom(pathName);
                ++count;
            }
            loader.logger.Info("{0} assemblies loaded.", count);
            return discoveredAssemblyLocations;
        }
#endif
        public static T TryLoadAndCreateInstance<T>(string assemblyName, Logger logger) where T : class
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                var foundType =
                    TypeUtils.GetTypes(
                        assembly,
                        type =>
                        typeof(T).IsAssignableFrom(type) && !type.GetTypeInfo().IsInterface
                        && type.GetConstructor(Type.EmptyTypes) != null, logger).FirstOrDefault();
                if (foundType == null)
                {
                    return null;
                }

                return (T)Activator.CreateInstance(foundType, true);
            }
            catch (FileNotFoundException exception)
            {
                logger.Warn(ErrorCode.Loader_TryLoadAndCreateInstance_Failure, exception.Message, exception);
                return null;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Loader_TryLoadAndCreateInstance_Failure, exc.Message, exc);
                throw;
            }
        }

        public static T LoadAndCreateInstance<T>(string assemblyName, Logger logger) where T : class
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                var foundType = TypeUtils.GetTypes(assembly, type => typeof(T).IsAssignableFrom(type), logger).First();

                return (T)Activator.CreateInstance(foundType, true);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Loader_LoadAndCreateInstance_Failure, exc.Message, exc);
                throw;
            }
        }

#if !NETSTANDARD_TODO
        // this method is internal so that it can be accessed from unit tests, which only test the discovery
        // process-- not the actual loading of assemblies.
        internal static AssemblyLoader NewAssemblyLoader(
                Dictionary<string, SearchOption> dirEnumArgs,
                IEnumerable<AssemblyLoaderPathNameCriterion> pathNameCriteria,
                IEnumerable<AssemblyLoaderReflectionCriterion> reflectionCriteria,
                Logger logger)
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
                        if (logger.IsVerbose) logger.Verbose("Trying to pre-load {0} to reflection-only context.", j);
                        Assembly.ReflectionOnlyLoadFrom(j);
                    }
                    catch (Exception)
                    {
                        if (logger.IsVerbose) logger.Verbose("Failed to pre-load assembly {0} in reflection-only context.", j);
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
            var matched = MatchWithLoadedAssembly(AssemblyName.GetAssemblyName(asmPathName));
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
                
                assembly = Assembly.ReflectionOnlyLoadFrom(pathName);
            }
            catch (FileLoadException e)
            {
                assembly = null;
                if (!InterpretFileLoadException(pathName, out complaints))
                    complaints = ReportUnexpectedException(e);

                if (RethrowDiscoveryExceptions)
                    throw;
                
                return false;
            }
            catch (Exception e)
            {
                assembly = null;
                complaints = ReportUnexpectedException(e);

                if (RethrowDiscoveryExceptions)
                    throw;
                
                return false;
            }

            complaints = null;
            return true;
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
            msg.Append(String.Format("User assembly ignored: {0}", pathName));
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
#endif
    }
}

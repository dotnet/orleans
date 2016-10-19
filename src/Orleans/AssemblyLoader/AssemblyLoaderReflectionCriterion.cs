#if !NETSTANDARD_TODO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{
    internal class AssemblyLoaderReflectionCriterion : AssemblyLoaderCriterion
    {
        internal delegate bool AssemblyPredicate(Assembly candidate, out IEnumerable<string> complaint);
        internal delegate bool TypePredicate(Type candidate, out IEnumerable<string> complaint);

        /// <summary>
        /// Create a new criterion that filters assemblies by predicate. 
        /// </summary>
        /// <param name="assemblyPredicate">A predicate which accepts an assembly an an argument. If this predicate returns true, the assembly will loaded and further inspection of the assembly with halt. If this predicate returns false, the predicate must provide a complaint explaining why the assembly does not meet the criterion described by the predicate.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">assemblyPredicate is null.</exception>
        internal static AssemblyLoaderReflectionCriterion NewCriterion(AssemblyPredicate assemblyPredicate)
        {
            if (assemblyPredicate == null)
                throw new ArgumentNullException("assemblyPredicate");
            return 
                new AssemblyLoaderReflectionCriterion(assemblyPredicate);
        }

        /// <summary>
        /// Create a new criterion that filters assemblies by predicate. 
        /// </summary>
        /// <param name="typePredicate">A predicate which accepts a reflection-only type as an argument. If this predicate returns true, the assembly that provides the specified type will loaded and further inspection of the assembly with halt. If this predicate returns false, the predicate may provide a complaint explaining why the assembly does not meet the criterion described by the predicate.</param>
        /// <param name="defaultComplaints">If no predicate provides a complaint, then these default complaints are logged instead.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">assemblyPredicate is null.</exception>
        internal static AssemblyLoaderReflectionCriterion NewCriterion(TypePredicate typePredicate, IEnumerable<string> defaultComplaints)
        {
            CheckComplaintQuality(defaultComplaints);

            return NewCriterion(
                    (Assembly assembly, out IEnumerable<string> assemblyComplaints) =>
                    {
                        TypeInfo[] types;
                        try
                        {
                            types = TypeUtils.GetDefinedTypes(assembly, null).ToArray();
                        }
                        catch (ReflectionTypeLoadException e)
                        {
                            if (InterceptReflectionTypeLoadException(e, out assemblyComplaints))
                                return false;
                            
                            // the default representation of a ReflectionTypeLoadException isn't very helpful
                            // to the user, so we flatten it into an AggregateException.
                            throw e.Flatten();
                        }

                        List<string> complaints = new List<string>();
                        foreach (var type in types)
                        {
                            IEnumerable<string> typeComplaints;
                            if (typePredicate(type, out typeComplaints))
                            {
                                //  we found a match! load the assembly.
                                assemblyComplaints = null;
                                return true;
                            }
                            if (typeComplaints != null)
                            {
                                complaints.AddRange(typeComplaints);
                            }
                        }

                        if (complaints.Count == 0)
                        {
                            complaints.AddRange(defaultComplaints);
                        }
                        assemblyComplaints = complaints;
                        return false;  
                    });
        }

        /// <summary>
        /// Create a new criterion that filters assemblies by predicate. 
        /// </summary>
        /// <param name="typePredicate">A predicate which accepts a reflection-only type as an argument. If this predicate returns true, the assembly that provides the specified type will loaded and further inspection of the assembly with halt. If this predicate returns false, the predicate may provide a complaint explaining why the assembly does not meet the criterion described by the predicate.</param>
        /// <param name="defaultComplaint">If no predicate provides a complaint, then this default complaint is logged instead.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">assemblyPredicate is null.</exception>        
        internal static AssemblyLoaderReflectionCriterion NewCriterion(TypePredicate typePredicate, string defaultComplaint)
        {
            return NewCriterion(typePredicate, new [] { defaultComplaint });
        }

        private AssemblyLoaderReflectionCriterion(AssemblyPredicate predicate) :
            base((object input, out IEnumerable<string> complaints) =>
                    predicate((Assembly)input, out complaints))
        {}

        private static bool InterceptReflectionTypeLoadException(ReflectionTypeLoadException outerException, out IEnumerable<string> complaints)
        {
            var badFiles = new Dictionary<string, string>();
            foreach (var exception in outerException.LoaderExceptions)
            {
                var fileNotFound = exception as FileNotFoundException;
                var fileLoadError = exception as FileLoadException;
                string fileName = null;
                if (fileNotFound != null)
                {
                    fileName = fileNotFound.FileName;
                }
                else if (fileLoadError != null)
                {
                    fileName = fileLoadError.FileName;
                }

                if (fileName != null)
                {
                    if (badFiles.ContainsKey(fileName)) 
                    {
                        // Don't overright first entry for this file, because it probably contains best error
                    }
                    else
                    {
                        badFiles.Add(fileName, exception.Message);
                    }
                }
                else
                {
                    // we haven't anticipated this specific exception, so rethrow.
                    complaints = null;
                    return false;
                }
            }            
            // experience shows that dependency errors in ReflectionTypeLoadExceptions tend to be redundant.
            // here, we ensure that each missing dependency is reported only once.
            complaints = 
                badFiles.Select(
                    (fileName, msg) =>
                        String.Format("An assembly dependency {0} could not be loaded: {1}", fileName, msg)).ToArray();
            // exception was anticipated.
            return true;
        }
    }
}
#endif
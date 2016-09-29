#if !NETSTANDARD_TODO
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{
    /// <summary>
    /// A subsystem interested in loading additional assemblies passes an instance
    /// of AssemblyLoadCriterion to AssemblyLoader.QualifySearch to ensure that
    /// assemblies that meet a given criterion are loaded into memory.
    /// </summary>
    internal abstract class AssemblyLoaderCriterion
    {
        /// <summary>
        /// An AssemblyLoadCriterion wraps a delegate where the predicate logic is implemented.
        /// </summary>
        /// <param name="complaints">If the candidate is not interesting to the subsystem that
        /// registered the criterion, the predicate must supply a complaint-- i.e. a message
        /// describing why the assembly wasn't interesting to the subsystem.</param>
        /// <param name="input">This either an absolute path name in the case of exclusion
        /// criteria, or an Assembly object in the case of load criterion.</param>
        /// <returns></returns>
        protected delegate bool Predicate(object input, out IEnumerable<string> complaints);

        private readonly Predicate predicate;

        protected AssemblyLoaderCriterion(Predicate predicate)
        {
            this.predicate = predicate;
        }

        /// <summary>
        /// AssemblyLoader invokes this wrapper for predicate when it needs to know whether an
        /// assembly is interesting to a subsystem that registered a criterion.
        /// </summary>
        /// <param name="complaints">The complaint, if the return value is *false*</param>
        /// <param name="input">The argument.</param>
        /// <returns>If T is a string, *false* indicates that the path name should be excluded from loading.
        /// If T is an assembly object, *true* indicates that the assembly should be loaded.</returns>
        /// <exception cref="System.InvalidOperationException">
        /// The predicate must provide a substantive complaint string if it returns *false*.</exception>
        public bool EvaluateCandidate(object input, out IEnumerable<string> complaints)
        {
            var isApproved = predicate(input, out complaints);
            if (isApproved)
            {
                return true;                
            }

            CheckComplaintQuality(complaints);
            return false;
        }

        public static void CheckComplaintQuality(IEnumerable<string> complaints)
        {
            if (complaints == null || !complaints.Any())
            {
                throw new ArgumentException("Predicates returning false must provide at least one complaint string.");                    
            }
            foreach (var s in complaints)
            {
                if (String.IsNullOrWhiteSpace(s))
                {
                    throw new InvalidOperationException("All complaint strings must be substantive.");     
                }
            }
        }
    }
}
#endif
using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class AssemblyLoaderPathNameCriterion : AssemblyLoaderCriterion
    {
        internal new delegate bool Predicate(string pathName, out IEnumerable<string> complaints);

        internal static AssemblyLoaderPathNameCriterion NewCriterion(Predicate predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException("predicate");

            return new AssemblyLoaderPathNameCriterion(predicate);
        }

        // constructor used by serializator
        private AssemblyLoaderPathNameCriterion() : base(null) { }

        private AssemblyLoaderPathNameCriterion(Predicate predicate) :
            base((object input, out IEnumerable<string> complaints) =>
                    predicate((string)input, out complaints))
        {}
    }
}

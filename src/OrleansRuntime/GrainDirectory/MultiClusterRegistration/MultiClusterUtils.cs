using System;

namespace Orleans.Runtime.GrainDirectory
{
    internal class MultiClusterUtils
    {
        /// <summary>
        /// Precedence function to resolve races among clusters that are trying to create an activation for a particular grain.
        /// </summary>
        /// <param name="grain">The GrainID under consideration.</param>
        /// <param name="clusterLeft"></param>
        /// <param name="clusterRight"></param>
        /// <returns>
        /// The function returns "true" if clusterLeft has precedence over clusterRight.
        /// </returns>
        internal static bool ActivationPrecedenceFunc(GrainId grain, string clusterLeft, string clusterRight)
        {
            // Make sure that we're not calling this function with default cluster identifiers.
            if (clusterLeft == null || clusterRight == null)
            {
                throw new OrleansException("ActivationPrecedenceFunction must be called with valid cluster identifiers.");
            }

            // use string comparison for cluster precedence, with polarity based on uniform grain hash
            if (grain.GetUniformHashCode() % 2 == 0)
                return string.Compare(clusterLeft, clusterRight, StringComparison.Ordinal) < 0;
            else
                return string.Compare(clusterRight, clusterLeft, StringComparison.Ordinal) < 0;
        }

    }
}

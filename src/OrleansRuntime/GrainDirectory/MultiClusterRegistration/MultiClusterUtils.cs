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

            var precLeft = grain.GetUniformHashCode() ^ clusterLeft.GetHashCode();
            var precRight = grain.GetUniformHashCode() ^ clusterRight.GetHashCode();
            return (precLeft < precRight) || (precLeft == precRight && (string.Compare(clusterLeft, clusterRight, StringComparison.Ordinal) < 0));
        }

    }
}

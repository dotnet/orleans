using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for grain classes.
    /// </summary>
    public class GrainClassOptions
    {
        /// <summary>
        /// Gets the list of grain classes which are excluded from the silo.
        /// </summary>
        public List<string> ExcludedGrainTypes { get; } = new List<string>();
    }
}

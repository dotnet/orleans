using System;

namespace Orleans.Runtime.Configuration
{
    /// <summary>Configures the Grain-based membership options</summary>
    public class GrainBasedMembershipOptions
    {
        /// <summary>
        /// Gets or sets the seed node to find the membership system grain.
        /// </summary>
        public Uri SeedNode { get; set; }
    }
}
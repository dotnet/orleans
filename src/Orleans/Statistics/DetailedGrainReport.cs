using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    [Serializable]
    internal class DetailedGrainReport
    {
        public GrainId Grain { get; set; }
        /// <summary>silo on which these statistics come from</summary>
        public SiloAddress SiloAddress { get; set; }
        /// <summary>silo on which these statistics come from</summary>
        public string SiloName { get; set; }
        /// <summary>activation addresses in the local directory cache</summary>
        public List<ActivationAddress> LocalCacheActivationAddresses { get; set; }
        /// <summary>activation addresses in the local directory.</summary>
        public List<ActivationAddress> LocalDirectoryActivationAddresses { get; set; }
        /// <summary>primary silo for this grain</summary>
        public SiloAddress PrimaryForGrain { get; set; }
        /// <summary>the name of the class that implements this grain.</summary>
        public string GrainClassTypeName { get; set; }
        /// <summary>activations on this silo</summary>
        public List<string> LocalActivations { get; set; }

        public override string ToString()
        {
            return string.Format(Environment.NewLine 
                                 + "**DetailedGrainReport for grain {0} from silo {1} SiloAddress={2}" + Environment.NewLine 
                                 + "   LocalCacheActivationAddresses={3}" + Environment.NewLine
                                 + "   LocalDirectoryActivationAddresses={4}"  + Environment.NewLine
                                 + "   PrimaryForGrain={5}" + Environment.NewLine 
                                 + "   GrainClassTypeName={6}" + Environment.NewLine
                                 + "   LocalActivations:" + Environment.NewLine
                                 + "{7}." + Environment.NewLine,
                Grain.ToDetailedString(),                                   // {0}
                SiloName,                                                   // {1}
                SiloAddress.ToLongString(),                                 // {2}
                Utils.EnumerableToString(LocalCacheActivationAddresses),    // {3}
                Utils.EnumerableToString(LocalDirectoryActivationAddresses),// {4}
                PrimaryForGrain,                                            // {5}
                GrainClassTypeName,                                         // {6}
                Utils.EnumerableToString(LocalActivations,                  // {7}
                    str => string.Format("      {0}", str), "\n"));
        }
    }
}
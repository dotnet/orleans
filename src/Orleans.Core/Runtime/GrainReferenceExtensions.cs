using System;

namespace Orleans.Runtime
{
    public static class GrainReferenceExtensions
    {
        public static string ToShortKeyString(this GrainReference grainRef)
        {
            if (grainRef.IsObserverReference)
            {
                return String.Format("{0}_{1}", grainRef.GrainId.ToParsableString(), grainRef.ObserverId.ToParsableString());
            }
            if (grainRef.IsSystemTarget)
            {
                return String.Format("{0}_{1}", grainRef.GrainId.ToParsableString(), grainRef.SystemTargetSilo.ToParsableString());
            }
            if (grainRef.HasGenericArgument)
            {
                return String.Format("{0}_{1}", grainRef.GrainId.ToParsableString(), grainRef.GenericArguments);
            }
            return String.Format("{0}", grainRef.GrainId.ToParsableString());
        }
    }
}

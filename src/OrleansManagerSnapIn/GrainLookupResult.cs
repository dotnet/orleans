using System;

namespace OrleansManager
{
    [Serializable]
    public struct GrainLookupResult : IEquatable<GrainLookupResult>
    {
        public string SiloAddress { get; set; }
        public string ActivationId { get; set; }
        
        #region Equality

        public bool Equals(GrainLookupResult other)
        {
            return string.Equals(SiloAddress, other.SiloAddress)
                && string.Equals(ActivationId, other.ActivationId);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            return obj is GrainLookupResult 
                && Equals((GrainLookupResult)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (SiloAddress == null ? 0 : SiloAddress.GetHashCode() * 397)
                    ^ (ActivationId == null ? 0 : ActivationId.GetHashCode());
            }
        }
        
        public static bool operator ==(GrainLookupResult left, GrainLookupResult right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(GrainLookupResult left, GrainLookupResult right)
        {
            return !left.Equals(right);
        }

        #endregion
    }
}
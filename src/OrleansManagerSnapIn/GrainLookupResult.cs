using System;

namespace OrleansManager
{
    [Serializable]
    public struct GrainLookupResult : IEquatable<GrainLookupResult>
    {
        public string SiloAddress { get; }
        public string ActivationId { get; }

        public GrainLookupResult(string siloAddress, string activationId)
        {
            SiloAddress = siloAddress;
            ActivationId = activationId;
        }

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

            return obj is GrainLookupResult && Equals((GrainLookupResult)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((SiloAddress?.GetHashCode() ?? 0) * 397)
                    ^ (ActivationId?.GetHashCode() ?? 0);
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
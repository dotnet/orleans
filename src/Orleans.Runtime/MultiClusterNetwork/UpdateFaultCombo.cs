using System;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal struct UpdateFaultCombo : IComparable
    {
        public readonly int UpdateZone;
        public readonly int FaultZone;

        public UpdateFaultCombo(int updateZone, int faultZone)
        {
            UpdateZone = updateZone;
            FaultZone = faultZone;
        }

        public UpdateFaultCombo(MembershipEntry e)
        {
            UpdateZone = e.UpdateZone;
            FaultZone = e.FaultZone;
        }

        public int CompareTo(object x)
        {
            var other = (UpdateFaultCombo)x;
            int comp = UpdateZone.CompareTo(other.UpdateZone);
            if (comp != 0) return comp;
            return FaultZone.CompareTo(other.FaultZone);
        }

        public override string ToString()
        {
            return $"({UpdateZone},{FaultZone})";
        }
    }
}

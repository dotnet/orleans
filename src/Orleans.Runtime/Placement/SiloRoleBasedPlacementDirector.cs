using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.MembershipService;

namespace Orleans.Runtime.Placement
{
    internal class SiloRoleBasedPlacementDirector : IPlacementDirector
    {
        private readonly IMembershipManager membershipManager;

        public SiloRoleBasedPlacementDirector(MembershipTableManager membershipTableManager)
        {
            this.membershipManager = membershipTableManager;
        }

        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var siloRole = target.GrainIdentity.Key.ToString();

            var compatibleSilos = membershipManager.CurrentSnapshot.Entries
                .Where(s => s.Value.Status == SiloStatus.Active && s.Value.RoleName == siloRole)
                .Select(s => s.Key)
                .Intersect(context.GetCompatibleSilos(target))
                .ToArray();

            if (compatibleSilos == null || compatibleSilos.Length == 0)
            {
                throw new OrleansException($"Cannot place grain with RoleName {siloRole}. Either Role name is invalid or there are no active silos with type {siloRole} in MembershipTableSnapshot registered yet.");
            }

            // If a valid placement hint was specified, use it.
            if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
            {
                return Task.FromResult(placementHint);
            }

            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }
    }
}

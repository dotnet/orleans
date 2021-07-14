using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Internal;
using Orleans.Runtime.MembershipService;

namespace Orleans.Runtime.Placement
{
    internal class SiloRoleBasedPlacementDirector : IPlacementDirector
    {
        private readonly MembershipTableManager membershipTableManager;
        private static readonly SafeRandom random = new SafeRandom();

        public SiloRoleBasedPlacementDirector(MembershipTableManager membershipTableManager)
        {
            this.membershipTableManager = membershipTableManager;
        }

        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            string siloRole = target.GrainIdentity.PrimaryKeyString;

            List<SiloAddress> siloAddressesSameRole = membershipTableManager.MembershipTableSnapshot.Entries
                .Where(s => s.Value.Status == SiloStatus.Active && s.Value.RoleName == siloRole)
                .Select(s => s.Key)
                .Intersect(context.GetCompatibleSilos(target))
                .ToList();

            if (siloAddressesSameRole == null || siloAddressesSameRole.Count == 0)
            {
                throw new OrleansException($"Available: {membershipTableManager.MembershipTableSnapshot.Entries.FirstOrDefault().Value?.RoleName}. Cannot place grain with RoleName {siloRole}. Either Role name is invalid or there are no active silos with type {siloRole} in MembershipTableSnapshot registered yet.");
            }

            return Task.FromResult(siloAddressesSameRole[random.Next(siloAddressesSameRole.Count)]);
        }
    }
}

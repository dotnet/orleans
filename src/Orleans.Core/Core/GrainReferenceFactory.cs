using System;
using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans
{
    internal class GrainReferenceFactory
    {
        /// <summary>
        /// The mapping between concrete grain interface types and delegate
        /// </summary>
        private readonly ConcurrentDictionary<Type, GrainReferenceCaster> casters = new ConcurrentDictionary<Type, GrainReferenceCaster>();
        private readonly TypeMetadataCache typeCache;
        private readonly IGrainReferenceRuntime grainReferenceRuntime;

        /// <summary>
        /// Casts an <see cref="IAddressable"/> to a concrete <see cref="GrainReference"/> implementation.
        /// </summary>
        /// <param name="existingReference">The existing <see cref="IAddressable"/> reference.</param>
        /// <returns>The concrete <see cref="GrainReference"/> implementation.</returns>
        internal delegate object GrainReferenceCaster(IAddressable existingReference);

        public GrainReferenceFactory(TypeMetadataCache typeCache, IGrainReferenceRuntime grainReferenceRuntime)
        {
            this.typeCache = typeCache;
            this.grainReferenceRuntime = grainReferenceRuntime;
        }

        public object CreateGrainReference(Type interfaceType, GrainId grainId)
        {
            var untypedGrainReference = GrainReference.FromGrainId(
                grainId,
                this.grainReferenceRuntime,
                interfaceType.IsGenericType ? TypeUtils.GenericTypeArgsString(interfaceType.UnderlyingSystemType.FullName) : null);
            return this.Cast(untypedGrainReference, interfaceType);
        }

        public object Cast(IAddressable grain, Type interfaceType)
        {
            GrainReferenceCaster caster;
            if (!this.casters.TryGetValue(interfaceType, out caster))
            {
                // Create and cache a caster for the interface type.
                caster = this.casters.GetOrAdd(interfaceType, MakeCaster);
            }

            return caster(grain);

            GrainReferenceCaster MakeCaster(Type interfaceType)
            {
                var grainReferenceType = this.typeCache.GetGrainReferenceType(interfaceType);
                return GrainCasterFactory.CreateGrainReferenceCaster(interfaceType, grainReferenceType);
            }
        }
    }
}

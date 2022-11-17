using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace Migration.Tests
{
    internal static class TestExtensions
    {
        public static IGrain GetTestGrain(this IGrainFactory grainFactory, Type grainInterfaceType, string key, string keyExt)
        {
            IGrain grain;
            if (grainInterfaceType.IsAssignableTo(typeof(IGrainWithStringKey)))
            {
                grain = grainFactory.GetGrain(grainInterfaceType, key);
            }
            else if (grainInterfaceType.IsAssignableTo(typeof(IGrainWithGuidKey)))
            {
                var keyGuid = Guid.Parse(key);
                grain = grainFactory.GetGrain(grainInterfaceType, keyGuid);
            }
            else if (grainInterfaceType.IsAssignableTo(typeof(IGrainWithGuidCompoundKey)))
            {
                var keyGuid = Guid.Parse(key);
                grain = grainFactory.GetGrain(grainInterfaceType, keyGuid, keyExt);
            }
            else if (grainInterfaceType.IsAssignableTo(typeof(IGrainWithIntegerKey)))
            {
                var keyInt = long.Parse(key);
                grain = grainFactory.GetGrain(grainInterfaceType, keyInt);
            }
            else if (grainInterfaceType.IsAssignableTo(typeof(IGrainWithIntegerCompoundKey)))
            {
                var keyInt = long.Parse(key);
                grain = grainFactory.GetGrain(grainInterfaceType, keyInt, keyExt);
            }
            else
            {
                throw new ArgumentException("Unknown key type");
            }
            return grain;
        }
    }
}

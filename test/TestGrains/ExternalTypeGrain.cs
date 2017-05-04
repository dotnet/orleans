using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ExternalTypeGrain : Grain, IExternalTypeGrain
    {
        public Task GetAbstractModel(IEnumerable<NameObjectCollectionBase> list)
        {
            base.GetLogger().Verbose("GetAbstractModel: Success");
            return Task.CompletedTask;
        }

        public Task<EnumClass> GetEnumModel()
        {
            return Task.FromResult( new EnumClass() { EnumsList = new List<DateTimeKind>() { DateTimeKind.Local } });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    public class EnumClass
    {
        public IEnumerable<DateTimeKind> EnumsList { get; set; }
    }

    public interface IExternalTypeGrain : IGrainWithIntegerKey
    {
        Task GetAbstractModel(IEnumerable<NameObjectCollectionBase> list);

        Task<EnumClass> GetEnumModel();
    }
}

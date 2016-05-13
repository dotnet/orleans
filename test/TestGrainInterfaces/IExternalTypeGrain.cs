using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using System.Collections.Specialized;
using System;

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

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Orleans;
#if NETSTANDARD
using SerializableAttribute = Orleans.SerializableAttribute;
#endif

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

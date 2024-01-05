using System.Collections.Specialized;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [GenerateSerializer]
    public class EnumClass
    {
        [Id(0)]
        public IEnumerable<DateTimeKind> EnumsList { get; set; }
    }

    public interface IExternalTypeGrain : IGrainWithIntegerKey
    {
        Task GetAbstractModel(IEnumerable<NameObjectCollectionBase> list);

        Task<EnumClass> GetEnumModel();
    }
}

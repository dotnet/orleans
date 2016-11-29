using System.Threading.Tasks;


namespace Orleans.Runtime
{
    /// <summary>
    /// Client gateway interface for obtaining the grain interface/type map.
    /// </summary>
    internal interface ITypeManager : ISystemTarget
    {
        Task<IGrainTypeResolver> GetTypeCodeMap(SiloAddress silo);

        Task<Streams.ImplicitStreamSubscriberTable> GetImplicitStreamSubscriberTable(SiloAddress silo);
    }
}

using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface ISiloTypeManager : ISystemTarget
    {
        /// <summary>
        /// Acquires grain interface map for all grain types supported by hosted silo.
        /// </summary>
        /// <returns></returns>
        Task<GrainInterfaceMap> GetSiloTypeCodeMap();
    }
}
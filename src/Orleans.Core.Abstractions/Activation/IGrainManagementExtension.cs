using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public interface IGrainManagementExtension : IGrainExtension
    {
        Task DeactivateOnIdle();
    }
}

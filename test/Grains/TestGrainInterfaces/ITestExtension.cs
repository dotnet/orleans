using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface ITestExtension : IGrainExtension
    {
        Task<string> CheckExtension_1();

        Task<string> CheckExtension_2();
    }

    public interface IGenericTestExtension<T> : IGrainExtension
    {
        Task<T> CheckExtension_1();

        Task<string> CheckExtension_2();
    }

    public interface ISimpleExtension : IGrainExtension
    {
        Task<string> CheckExtension_1();
    }

    public interface IAutoExtension : IGrainExtension
    {
        Task<string> CheckExtension();
    }
}
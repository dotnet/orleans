
namespace Orleans.Runtime
{
    public interface IPersistentStateConfiguration
    {
        string StateName { get; }
        string StorageName { get; }
    }
}

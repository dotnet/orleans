using Orleans.Serialization.Activators;

namespace Orleans.Serialization.Serializers
{
    public interface IActivatorProvider
    {
        IActivator<T> GetActivator<T>();
    }
}
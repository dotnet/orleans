
namespace Orleans.Serialization.Activators
{
    public interface IActivator<T> : IActivator
    {
        T Create();
    }

    public interface IActivator<TArgs, TResult> : IActivator
    {
        TResult Create(TArgs arg);
    }

    public interface IActivator { }
}
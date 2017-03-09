namespace Orleans.Runtime
{
    internal interface ICounter<out T> : ICounter
    {
        T GetCurrentValue();
    }
}

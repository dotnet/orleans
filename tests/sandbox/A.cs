

using Orleans;

namespace A
{
    [Alias("A")]
    public interface IA : IGrainWithStringKey
    {
        [Alias("Void")] Task Void(string a);
        [Alias("Void")] Task Void(long a);
        [Alias("Int")] Task<int> Int(int a);
    }

    [Alias("B")]
    public interface IB : IGrainWithStringKey
    {
        [Alias("Void")] Task Void(string a);
        [Alias("Int")] Task<int> Int(int a);
    }
}

namespace B
{
    [Alias("A")]
    public interface IA : IGrainWithStringKey
    {
        [Alias("Int")] Task<int> Int(int a);
    }
}
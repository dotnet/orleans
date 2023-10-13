using Orleans;
using Orleans.Concurrency;

namespace N1;

//[Alias("A")]
public class MyClass { }

//[Alias("A")]
public class MyClass2
{
    //[Alias("B")] public static void V(uint a) { }
    //[Alias("B")] public static void V(int a) { }
}

[Alias("IC")]
public interface IC : IGrainWithStringKey
{
    [Alias("MyMethod")]
    [AlwaysInterleave]
    void MyMethod();
}


public class MyG : Grain, IC
{
    [AlwaysInterleave]
    public void MyMethod() => throw new NotImplementedException();
}
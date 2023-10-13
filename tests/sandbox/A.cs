using Orleans;

namespace N1;

[Alias("A")]
public class MyClass { }

[Alias("A")]
public class MyClass2
{
    [Alias("B")] public static void V(uint a) { }
    [Alias("B")] public static void V(int a) { }
}
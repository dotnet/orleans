using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using enums;

namespace ns1
{
    public enum e1 { }
    public class t1 { }
    public class gt1<T> { }
}

namespace ns2
{
    public enum e1 { }
    public class t1 { }
    public class gt1<T> { }
}

namespace enums
{
    public enum Enum1 {}
    public enum Enum2 { }
}

namespace UnitTestGrainInterfaces
{

    public interface IConflictingNamesGrain<U> : IGrain
    {
        Task Method1(ns1.e1 p1, ns2.e1 p2);

        Task<ns1.t1> Method1(ns2.t1 t1);
        Task<ns1.gt1<int>> Method2(ns2.t1 p1, ns1.t1 p2, ns1.gt1<int> p3);
        Task<ns1.gt1<U>> Method3(ns2.t1 p1, ns1.t1 p2, ns1.gt1<U> p3);
        Task<Immutable<Enum1>>  Method4(Immutable<Enum2> a);
    }
}

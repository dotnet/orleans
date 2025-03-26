using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace Migration.Tests
{
    public interface IBase
    {
        Task<bool> Foo();
    }

    public interface IMyStringGrain : IGrainWithStringKey, IBase { }

    public interface IMyGuidGrain : IGrainWithGuidKey, IBase { }

    public interface IMyGuidCompoundGrain : IGrainWithGuidCompoundKey, IBase { }

    public interface IMyIntegerGrain : IGrainWithIntegerKey, IBase { }

    public interface IMyIntegerCompoundGrain : IGrainWithIntegerCompoundKey, IBase { }

    public interface IMyStringGrain<T> : IGrainWithStringKey, IBase { }

    public interface IMyGuidGrain<T> : IGrainWithGuidKey, IBase { }

    public interface IMyGuidCompoundGrain<T> : IGrainWithGuidCompoundKey, IBase { }

    public interface IMyIntegerGrain<T> : IGrainWithIntegerKey, IBase { }

    public interface IMyIntegerCompoundGrain<T> : IGrainWithIntegerCompoundKey, IBase { }

    public interface IMyStringGrain<T, U> : IGrainWithStringKey, IBase { }

    public interface IMyGuidGrain<T, U> : IGrainWithGuidKey, IBase { }

    public interface IMyGuidCompoundGrain<T, U> : IGrainWithGuidCompoundKey, IBase { }

    public interface IMyIntegerGrain<T, U> : IGrainWithIntegerKey, IBase { }

    public interface IMyIntegerCompoundGrain<T, U> : IGrainWithIntegerCompoundKey, IBase { }

    public abstract class Base : Grain, IBase
    {
        public Task<bool> Foo() => Task.FromResult(true);
    }
    public class MyStringGrain : Base, IMyStringGrain { }

    public class MyGuidGrain : Base, IMyGuidGrain { }

    public class MyGuidCompoundGrain : Base, IMyGuidCompoundGrain { }

    public class MyIntegerGrain : Base, IMyIntegerGrain { }

    public class MyIntegerCompoundGrain : Base, IMyIntegerCompoundGrain { }

    public class MyStringGrain<T> : Base, IMyStringGrain<T> { }

    public class MyGuidGrain<T> : Base, IMyGuidGrain<T> { }

    public class MyGuidCompoundGrain<T> : Base, IMyGuidCompoundGrain<T> { }

    public class MyIntegerGrain<T> : Base, IMyIntegerGrain<T> { }

    public class MyIntegerCompoundGrain<T> : Base, IMyIntegerCompoundGrain<T> { }

    public class MyStringGrain<T, U> : Base, IMyStringGrain<T, U> { }

    public class MyGuidGrain<T, U> : Base, IMyGuidGrain<T, U> { }

    public class MyGuidCompoundGrain<T, U> : Base, IMyGuidCompoundGrain<T, U> { }

    public class MyIntegerGrain<T, U> : Base, IMyIntegerGrain<T, U> { }

    public class MyIntegerCompoundGrain<T, U> : Base, IMyIntegerCompoundGrain<T, U> { }
}

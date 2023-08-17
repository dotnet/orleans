// ReSharper disable InconsistentNaming
namespace Tester.CodeGenTests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Orleans;
    using Orleans.Providers;

    public interface IGrainWithGenericMethods : IGrainWithGuidKey
    {
        Task<Type[]> GetTypesExplicit<T, U, V>();
        Task<Type[]> GetTypesInferred<T, U, V>(T t, U u, V v);
        Task<Type[]> GetTypesInferred<T, U>(T t, U u, int v);
        Task<T> RoundTrip<T>(T val);
        Task<int> RoundTrip(int val);
        Task<T> Default<T>();
        Task<string> Default();
        Task<TGrain> Constraints<TGrain>(TGrain grain) where TGrain : IGrain;
        Task SetValueOnObserver<T>(IGrainObserverWithGenericMethods observer, T value);
        ValueTask<int> ValueTaskMethod(bool useCache);
    }

    public interface IGrainObserverWithGenericMethods : IGrainObserver
    {
        void SetValue<T>(T value);
    }

    public class GrainWithGenericMethods : Grain, IGrainWithGenericMethods
    {
        private object state;

        public Task<Type[]> GetTypesExplicit<T, U, V>()
        {
            return Task.FromResult(new[] {typeof(T), typeof(U), typeof(V)});
        }

        public Task<Type[]> GetTypesInferred<T, U, V>(T t, U u, V v)
        {
            return Task.FromResult(new[] { typeof(T), typeof(U), typeof(V) });
        }

        public Task<Type[]> GetTypesInferred<T, U>(T t, U u, int v)
        {
            return Task.FromResult(new[] { typeof(T), typeof(U) });
        }

        public Task<T> RoundTrip<T>(T val)
        {
            return Task.FromResult(val);
        }

        public Task<int> RoundTrip(int val)
        {
            return Task.FromResult(-val);
        }

        public Task<T> Default<T>()
        {
            return Task.FromResult(default(T));
        }

        public Task<string> Default()
        {
            return Task.FromResult("default string");
        }

        public Task<TGrain> Constraints<TGrain>(TGrain grain) where TGrain : IGrain
        {
            return Task.FromResult(grain);
        }

        public void SetValue<T>(T value)
        {
            this.state = value;
        }

        public Task<T> GetValue<T>() => Task.FromResult((T) this.state);

        public Task SetValueOnObserver<T>(IGrainObserverWithGenericMethods observer, T value)
        {
            observer.SetValue<T>(value);
            return Task.FromResult(0);
        }

        public ValueTask<int> ValueTaskMethod(bool useCache)
        {
            if (useCache)
            {
                return new ValueTask<int>(1);
            }

            return new ValueTask<int>(Task.FromResult(2));
        }
    }

    public interface IGenericGrainWithGenericMethods<T> : IGrainWithGuidKey
    {
        Task<T> Method(T value);
#pragma warning disable 693
        Task<T> Method<T>(T value);
#pragma warning restore 693
    }

    public class GrainWithGenericMethods<T> : Grain, IGenericGrainWithGenericMethods<T>
    {
        public Task<T> Method(T value) => Task.FromResult(default(T));

#pragma warning disable 693
        public Task<T> Method<T>(T value) => Task.FromResult(value);
#pragma warning restore 693
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class RuntimeGenericGrain : Grain<GenericGrainState<@event>>, IRuntimeCodeGenGrain<@event>
    {
        public Task<@event> SetState(@event value)
        {
            this.State.@event = value;
            return Task.FromResult(this.State.@event);
        }

        public Task<@event> @static()
        {
            return Task.FromResult(this.State.@event);
        }
    }

    public interface IRuntimeCodeGenGrain<T> : IGrainWithGuidKey
    {
        /// <summary>
        /// Sets and returns the grain's state.
        /// </summary>
        /// <param name="value">The new state.</param>
        /// <returns>The current state.</returns>
        Task<T> SetState(T value);

        /// <summary>
        /// Tests that code generation correctly handles methods with reserved keyword identifiers.
        /// </summary>
        /// <returns>The current state's event.</returns>
        Task<@event> @static();
    }

    [Serializable]
    [GenerateSerializer]
    public class GenericGrainState<T>
    {
        [Id(0)]
        public T @event { get; set; }
    }

    /// <summary>
    /// A class designed to test that code generation correctly handles reserved keywords.
    /// </summary>
    [GenerateSerializer]
    public class @event : IEquatable<@event>
    {
        private static readonly IEqualityComparer<@event> EventComparerInstance = new EventEqualityComparer();

        public enum @enum
        {
            @async,
            @int,
        }

        /// <summary>
        /// A public field.
        /// </summary>
        [Id(0)]
        public Guid Id;

        /// <summary>
        /// A private field.
        /// </summary>
        [Id(1)]
        private Guid privateId;

        /// <summary>
        /// A property with a reserved keyword type and identifier.
        /// </summary>
        [Id(2)]
        public @event @public { get; set; }

        /// <summary>
        /// Gets or sets the enum.
        /// </summary>
        [Id(3)]
        public @enum Enum { get; set; }

        /// <summary>
        /// A property with a reserved keyword generic type and identifier.
        /// </summary>
        [Id(4)]
        public List<@event> @if { get; set; }

        public static IEqualityComparer<@event> EventComparer
        {
            get
            {
                return EventComparerInstance;
            }
        }

        /// <summary>
        /// Gets or sets the private id.
        /// </summary>
        // ReSharper disable once ConvertToAutoProperty
        public Guid PrivateId
        {
            get
            {
                return this.privateId;
            }

            set
            {
                this.privateId = value;
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return this.Equals((@event)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (this.@if != null ? this.@if.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (this.@public != null ? this.@public.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ this.privateId.GetHashCode();
                hashCode = (hashCode * 397) ^ this.Id.GetHashCode();
                return hashCode;
            }
        }

        public bool Equals(@event other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            if (this.@if != other.@if)
            {
                if (this.@if != null && !this.@if.SequenceEqual(other.@if, EventComparer))
                {
                    return false;
                }
            }

            if (!Equals(this.@public, other.@public))
            {
                if (this.@public != null && !this.@public.Equals(other.@public))
                {
                    return false;
                }
            }

            return this.privateId.Equals(other.privateId) && this.Id.Equals(other.Id) && this.Enum == other.Enum;
        }

        private sealed class EventEqualityComparer : IEqualityComparer<@event>
        {
            public bool Equals(@event x, @event y)
            {
                return x.Equals(y);
            }

            public int GetHashCode(@event obj)
            {
                return obj.GetHashCode();
            }
        }
    }

    [GenerateSerializer]
    public class NestedGeneric<T>
    {
        [Id(0)]
        public Nested Payload { get; set; }

        [GenerateSerializer]
        public class Nested
        {
            [Id(0)]
            public T Value { get; set; }
        }
    }

    [GenerateSerializer]
    public class NestedConstructedGeneric
    {
        [Id(0)]
        public Nested<int> Payload { get; set; }

        [GenerateSerializer]
        public class Nested<T>
        {
            [Id(0)]
            public T Value { get; set; }
        }
    }

    public interface INestedGenericGrain : IGrainWithGuidKey
    {
        Task<int> Do(NestedGeneric<int> value);
        Task<int> Do(NestedConstructedGeneric value);
    }

    /// <summary>
    /// Tests that nested classes do not fail code generation.
    /// </summary>
    public class NestedGenericGrain : Grain,  INestedGenericGrain
    {
        public Task<int> Do(NestedGeneric<int> value)
        {
            return Task.FromResult(value.Payload.Value);
        }

        public Task<int> Do(NestedConstructedGeneric value)
        {
            return Task.FromResult(value.Payload.Value);
        }
    }

    public interface IGrainWithStaticMembers : IGrainWithGuidKey
    {
        public static int StaticMethodWithNonAsyncReturnType(int a) => 0;
        public static virtual int StaticVirtualMethodWithNonAsyncReturnType(int a) => 0;
        public static int StaticProperty => 0;
        public static virtual int StaticVirtualProperty => 0;
        public static int StaticMethodWithOutAndVarParams(out int a, ref int b) { a = 0; return 0; }
        public static virtual int StaticVirtualMethodWithOutAndVarParams(out int a, ref int b) { a = 0; return 0; }
    }

    public class GrainWithStaticMembers : Grain, IGrainWithStaticMembers
    { }
}

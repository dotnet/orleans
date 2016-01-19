// ReSharper disable InconsistentNaming
namespace Tester.CodeGenTests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using Orleans;
    using Orleans.Providers;

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
    public class GenericGrainState<T>
    {
        public T @event { get; set; }
    }

    /// <summary>
    /// A class designed to test that code generation correctly handles reserved keywords.
    /// </summary>
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
        public Guid Id;

        /// <summary>
        /// A private field.
        /// </summary>
        private Guid privateId;

        /// <summary>
        /// A property with a reserved keyword type and identifier.
        /// </summary>
        public @event @public { get; set; }

        /// <summary>
        /// Gets or sets the enum.
        /// </summary>
        public @enum Enum { get; set; }

        /// <summary>
        /// A property with a reserved keyword generic type and identifier.
        /// </summary>
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
                var x = typeof(NestedGeneric<int>.Nested);

                return obj.GetHashCode();
            }
        }
    }

    public class NestedGeneric<T>
    {
        public Nested Payload { get; set; }

        public class Nested
        {
            public T Value { get; set; }
        }
    }

    public class NestedConstructedGeneric
    {
        public Nested<int> Payload { get; set; }

        public class Nested<T>
        {
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
}

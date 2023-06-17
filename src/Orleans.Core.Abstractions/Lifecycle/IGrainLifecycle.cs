#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// The observable grain lifecycle.
    /// </summary>
    /// <remarks>
    /// This type is usually used as the generic parameter in <see cref="ILifecycleParticipant{IGrainLifecycle}"/> as
    /// a means of participating in the lifecycle stages of a grain activation.
    /// </remarks>
    public interface IGrainLifecycle : ILifecycleObservable
    {
        /// <summary>
        /// Registers a grain migration participant.
        /// </summary>
        /// <param name="participant">The participant.</param>
        void AddMigrationParticipant(IGrainMigrationParticipant participant);

        /// <summary>
        /// Unregisters a grain migration participant.
        /// </summary>
        /// <param name="participant">The participant.</param>
        void RemoveMigrationParticipant(IGrainMigrationParticipant participant);
    }

    public interface IGrainMigrationParticipant
    {
        /// <summary>
        /// Called on the original activation when migration is initiated, after <see cref="IGrainBase.OnDeactivateAsync(DeactivationReason, CancellationToken)"/> completes.
        /// The participant can access and update the dehydration context.
        /// </summary>
        /// <param name="dehydrationContext">The dehydration context.</param>
        void OnDehydrate(IDehydrationContext dehydrationContext);

        /// <summary>
        /// Called on the new activation after a migration, before <see cref="IGrainBase.OnActivateAsync(CancellationToken)"/> is called.
        /// The participant can restore state from the migration context.
        /// </summary>
        /// <param name="rehydrationContext">The rehydration context.</param>
        void OnRehydrate(IRehydrationContext rehydrationContext);
    }

    /// <summary>
    /// Records the state of a grain activation which is in the process of being dehydrated for migration to another location.
    /// </summary>
    public interface IDehydrationContext
    {
        /// <summary>
        /// Gets the keys in the context.
        /// </summary>
        IEnumerable<string> Keys { get; }

        /// <summary>
        /// Adds a sequence of bytes to the dehydration context, associating the sequence with the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        void AddBytes(string key, ReadOnlySpan<byte> value);

        /// <summary>
        /// Adds a sequence of bytes to the dehydration context, associating the sequence with the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="valueWriter">A delegate used to write the provided value to the context.</param>
        /// <param name="value">The value to provide to <paramref name="valueWriter"/>.</param>
        void AddBytes<T>(string key, Action<T, IBufferWriter<byte>> valueWriter, T value);

        /// <summary>
        /// Attempts to a value to the dehydration context, associated with the provided key, serializing it using <see cref="Orleans.Serialization.Serializer"/>.
        /// If a serializer is found for the value, and the key has not already been added, then the value is added and the method returns <see langword="true"/>.
        /// If no serializer exists or the key has already been added, then the value is not added and the method returns <see langword="false"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value to add.</param>
        bool TryAddValue<T>(string key, T? value);
    }

    /// <summary>
    /// Contains the state of a grain activation which is in the process of being rehydrated after moving from another location.
    /// </summary>
    public interface IRehydrationContext
    {
        /// <summary>
        /// Gets the keys in the context.
        /// </summary>
        IEnumerable<string> Keys { get; }

        /// <summary>
        /// Tries to get a sequence of bytes from the rehydration context, associated with the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value, if present.</param>
        /// <returns><see langword="true"/> if the key exists in the context, otherwise <see langword="false"/>.</returns>
        bool TryGetBytes(string key, out ReadOnlySequence<byte> value);

        /// <summary>
        /// Tries to get a value from the rehydration context, associated with the provided key, deserializing it using <see cref="Orleans.Serialization.Serializer"/>.
        /// If a serializer is found for the value, and the key is present, then the value is deserialized and the method returns <see langword="true"/>.
        /// If no serializer exists or the key has already been added, then the value is not added and the method returns <see langword="false"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value, if present.</param>
        /// <returns><see langword="true"/> if the key exists in the context, otherwise <see langword="false"/>.</returns>
        bool TryGetValue<T>(string key, out T? value);
    }
}

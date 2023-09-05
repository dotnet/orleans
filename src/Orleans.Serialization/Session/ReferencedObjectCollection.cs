using Orleans.Serialization.Codecs;
using Orleans.Serialization.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
#if NET6_0_OR_GREATER
using System.Reflection.Metadata;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// A collection of objects which are referenced while serializing, deserializing, or copying.
    /// </summary>
    public sealed class ReferencedObjectCollection
    {
        private struct ReferencePair
        {
            public ReferencePair(uint id, object @object)
            {
                Id = id;
                Object = @object;
            }

            public uint Id;
            public object Object;
        }

        /// <summary>
        /// Gets or sets the reference to object count.
        /// </summary>
        /// <value>The reference to object count.</value>
        internal int ReferenceToObjectCount;
        private readonly ReferencePair[] _referenceToObject = new ReferencePair[64];

        private int _objectToReferenceCount;
        private readonly ReferencePair[] _objectToReference = new ReferencePair[64];

        private Dictionary<uint, object> _referenceToObjectOverflow;
        private Dictionary<object, uint> _objectToReferenceOverflow;
        private uint _currentReferenceId;

        /// <summary>
        /// Tries to get the referenced object with the specified id.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The referenced object with the specified id if found, <see langword="null" /> otherwise.</returns>
        public object TryGetReferencedObject(uint reference)
        {
            var refs = _referenceToObject.AsSpan(0, ReferenceToObjectCount);
            for (int i = 0; i < refs.Length; ++i)
            {
                if (refs[i].Id == reference)
                    return refs[i].Object;
            }

            if (_referenceToObjectOverflow is { } overflow && overflow.TryGetValue(reference, out var value))
                return value;

            return null;
        }

        /// <summary>
        /// Marks a value field.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkValueField() => ++_currentReferenceId;

        internal uint CreateRecordPlaceholder() => ++_currentReferenceId;

        /// <summary>
        /// Gets or adds a reference.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="reference">The reference.</param>
        /// <returns><see langword="true" /> if a reference already existed, <see langword="false" /> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetOrAddReference(object value, out uint reference)
        {
            // Unconditionally bump the reference counter since a call to this method signifies a potential reference.
            var nextReference = ++_currentReferenceId;

            // Null is always at reference 0
            if (value is null)
            {
                reference = 0;
                return true;
            }

            var objects = _objectToReference.AsSpan(0, _objectToReferenceCount);
            for (int i = 0; i < objects.Length; ++i)
            {
                if (objects[i].Object == value)
                {
                    reference = objects[i].Id;
                    return true;
                }
            }

            if (_objectToReferenceOverflow is { } overflow)
            {
#if NET6_0_OR_GREATER
                ref var refValue = ref CollectionsMarshal.GetValueRefOrAddDefault(overflow, value, out var exists);
                if (exists)
                {
                    reference = refValue;
                    return true;
                }

                refValue = nextReference;
                Unsafe.SkipInit(out reference);
                return false;
#else
                if (overflow.TryGetValue(value, out var existing))
                {
                    reference = existing;
                    return true;
                }
                else
                {
                    overflow[value] = nextReference;
                    Unsafe.SkipInit(out reference);
                    return false;
                }
#endif
            }

            // Add the reference.
            var objectsArray = _objectToReference;
            var objectsCount = _objectToReferenceCount;
            if ((uint)objectsCount < (uint)objectsArray.Length)
            {
                _objectToReferenceCount = objectsCount + 1;
                objectsArray[objectsCount].Id = nextReference;
                objectsArray[objectsCount].Object = value;
            }
            else
            {
                CreateObjectToReferenceOverflow(value);
            }

            Unsafe.SkipInit(out reference);
            return false;
        }

        /// <summary>
        /// Gets the index of the reference, or <c>-1</c> if the object has not been encountered before.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The index of the reference, or <c>-1</c> if the object has not been encountered before.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetReferenceIndex(object value)
        {
            if (value is null)
            {
                return -1;
            }

            var refs = _referenceToObject.AsSpan(0, ReferenceToObjectCount);
            for (int i = 0; i < refs.Length; ++i)
            {
                if (refs[i].Object == value)
                {
                    return i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateObjectToReferenceOverflow(object value)
        {
            var result = new Dictionary<object, uint>(_objectToReferenceCount * 2, ReferenceEqualsComparer.Default);
            var objects = _objectToReference;
            for (var i = 0; i < objects.Length; i++)
            {
                var record = objects[i];
                result[record.Object] = record.Id;
                objects[i] = default;
            }

            result[value] = _currentReferenceId;

            _objectToReferenceCount = 0;
            _objectToReferenceOverflow = result;
        }

        private void AddToReferences(object value, uint reference)
        {
            if (_referenceToObjectOverflow is { } overflow)
            {
#if NET6_0_OR_GREATER
                ref var refValue = ref CollectionsMarshal.GetValueRefOrAddDefault(overflow, reference, out var exists);
                if (exists && value is not UnknownFieldMarker && refValue is not UnknownFieldMarker)
                {
                    // Unknown field markers can be replaced once the type is known.
                    ThrowReferenceExistsException(reference);
                }

                refValue = value;
#else
                if (overflow.TryGetValue(reference, out var existing) && value is not UnknownFieldMarker && existing is not UnknownFieldMarker)
                {
                    // Unknown field markers can be replaced once the type is known.
                    ThrowReferenceExistsException(reference);
                }

                overflow[reference] = value;
#endif
            }
            else
            {
                var refs = _referenceToObject.AsSpan(0, ReferenceToObjectCount);
                for (var i = 0; i < refs.Length; i++)
                {
                    if (refs[i].Id == reference)
                    {
                        if (value is not UnknownFieldMarker && refs[i].Object is not UnknownFieldMarker)
                        {
                            // Unknown field markers can be replaced once the type is known.
                            ThrowReferenceExistsException(reference);
                        }
                        refs[i].Object = value;
                        return;
                    }
                }

                _referenceToObject[ReferenceToObjectCount++] = new ReferencePair(reference, value);

                if (ReferenceToObjectCount >= _referenceToObject.Length)
                {
                    CreateReferenceToObjectOverflow();
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void CreateReferenceToObjectOverflow()
            {
                var result = new Dictionary<uint, object>(ReferenceToObjectCount * 2);
                var refs = _referenceToObject.AsSpan(0, ReferenceToObjectCount);
                for (var i = 0; i < refs.Length; i++)
                {
                    var record = refs[i];
                    result[record.Id] = record.Object;
                    refs[i] = default;
                }

                ReferenceToObjectCount = 0;
                _referenceToObjectOverflow = result;
            }
        }

        [DoesNotReturn]
        private static void ThrowReferenceExistsException(uint reference) => throw new InvalidOperationException($"Reference {reference} already exists");

        /// <summary>
        /// Records a reference field.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordReferenceField(object value) => RecordReferenceField(value, ++_currentReferenceId);

        /// <summary>
        /// Records a reference field with the specified identifier.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="referenceId">The reference identifier.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordReferenceField(object value, uint referenceId)
        {
            if (value is null)
            {
                return;
            }

            AddToReferences(value, referenceId);
        }

        /// <summary>
        /// Copies the reference table.
        /// </summary>
        /// <returns>A copy of the reference table.</returns>
        public Dictionary<uint, object> CopyReferenceTable() => _referenceToObject.Take(ReferenceToObjectCount).ToDictionary(r => r.Id, r => r.Object);

        /// <summary>
        /// Copies the identifier table.
        /// </summary>
        /// <returns>A copy of the identifier table.</returns>
        public Dictionary<object, uint> CopyIdTable() => _objectToReference.Take(_objectToReferenceCount).ToDictionary(r => r.Object, r => r.Id);

        /// <summary>
        /// Gets or sets the current reference identifier.
        /// </summary>
        /// <value>The current reference identifier.</value>
        public uint CurrentReferenceId { get => _currentReferenceId; set => _currentReferenceId = value; }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            _referenceToObject.AsSpan(0, ReferenceToObjectCount).Clear();
            _objectToReference.AsSpan(0, _objectToReferenceCount).Clear();

            ReferenceToObjectCount = 0;
            _objectToReferenceCount = 0;
            CurrentReferenceId = 0;

            _referenceToObjectOverflow = null;
            _objectToReferenceOverflow = null;
        }
    }
}
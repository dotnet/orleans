using Orleans.Serialization.Codecs;
using Orleans.Serialization.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// A collection of objects which are referenced while serializing, deserializing, or copying.
    /// </summary>
    public sealed class ReferencedObjectCollection
    {
        private readonly struct ReferencePair
        {
            public ReferencePair(uint id, object @object)
            {
                Id = id;
                Object = @object;
            }

            public uint Id { get; }

            public object Object { get; }
        }

        /// <summary>
        /// Gets or sets the reference to object count.
        /// </summary>
        /// <value>The reference to object count.</value>
        public int ReferenceToObjectCount { get; set; }
        private readonly ReferencePair[] _referenceToObject = new ReferencePair[64];

        private int _objectToReferenceCount;
        private readonly ReferencePair[] _objectToReference = new ReferencePair[64];

        private Dictionary<uint, object> _referenceToObjectOverflow;
        private Dictionary<object, uint> _objectToReferenceOverflow;

        /// <summary>
        /// Tries to get the referenced object with the specified id.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="value">The value.</param>
        /// <returns><see langword="true" /> if there was a referenced object with the specified id, <see langword="false" /> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetReferencedObject(uint reference, [NotNullWhen(true)] out object value)
        {
            // Reference 0 is always null.
            if (reference == 0)
            {
                value = null;
                return true;
            }

            for (int i = 0; i < ReferenceToObjectCount; ++i)
            {
                if (_referenceToObject[i].Id == reference)
                {
                    value = _referenceToObject[i].Object;
                    return true;
                }
            }

            if (_referenceToObjectOverflow is { } overflow)
            {
                return overflow.TryGetValue(reference, out value);
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Marks a value field.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkValueField() => ++CurrentReferenceId;

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
            var nextReference = ++CurrentReferenceId;

            // Null is always at reference 0
            if (value is null)
            {
                reference = 0;
                return true;
            }

            for (int i = 0; i < _objectToReferenceCount; ++i)
            {
                if (ReferenceEquals(_objectToReference[i].Object, value))
                {
                    reference = _objectToReference[i].Id;
                    return true;
                }
            }

            if (_objectToReferenceOverflow is { } overflow)
            {
                if (overflow.TryGetValue(value, out var existing))
                {
                    reference = existing;
                    return true;
                }
                else
                {
                    reference = nextReference;
                    overflow[value] = reference;
                }
            }

            // Add the reference.
            reference = nextReference;
            AddToReferenceToIdMap(value, reference);
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

            for (var i = 0; i < ReferenceToObjectCount; ++i)
            {
                if (ReferenceEquals(_referenceToObject[i].Object, value))
                {
                    return i;
                }
            }

            return -1;
        }


        private void AddToReferenceToIdMap(object value, uint reference)
        {
            if (_objectToReferenceOverflow is { } overflow)
            {
                overflow[value] = reference;
            }
            else
            {
                _objectToReference[_objectToReferenceCount++] = new ReferencePair(reference, value);

                if (_objectToReferenceCount >= _objectToReference.Length)
                {
                    CreateObjectToReferenceOverflow();
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void CreateObjectToReferenceOverflow()
            {
                var result = new Dictionary<object, uint>(_objectToReferenceCount * 2, ReferenceEqualsComparer.Default);
                for (var i = 0; i < _objectToReferenceCount; i++)
                {
                    var record = _objectToReference[i];
                    result[record.Object] = record.Id;
                    _objectToReference[i] = default;
                }

                _objectToReferenceCount = 0;
                _objectToReferenceOverflow = result;
            }
        }

        private void AddToReferences(object value, uint reference)
        {
            if (TryGetReferencedObject(reference, out var existing) && !(existing is UnknownFieldMarker) && !(value is UnknownFieldMarker))
            {
                // Unknown field markers can be replaced once the type is known.
                ThrowReferenceExistsException(reference);
                return;
            }

            if (_referenceToObjectOverflow is { } overflow)
            {
                overflow[reference] = value;
            }
            else
            {
                _referenceToObject[ReferenceToObjectCount++] = new ReferencePair(reference, value);

                if (ReferenceToObjectCount >= _referenceToObject.Length)
                {
                    CreateReferenceToObjectOverflow();
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void CreateReferenceToObjectOverflow()
            {
                var result = new Dictionary<uint, object>();
                for (var i = 0; i < ReferenceToObjectCount; i++)
                {
                    var record = _referenceToObject[i];
                    result[record.Id] = record.Object;
                    _referenceToObject[i] = default;
                }

                ReferenceToObjectCount = 0;
                _referenceToObjectOverflow = result;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowReferenceExistsException(uint reference) => throw new InvalidOperationException($"Reference {reference} already exists");

        /// <summary>
        /// Records a reference field.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordReferenceField(object value) => RecordReferenceField(value, ++CurrentReferenceId);

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
        public uint CurrentReferenceId { get; set; }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            var refToObj = _referenceToObject.AsSpan(0, Math.Min(_referenceToObject.Length, ReferenceToObjectCount));
            for (var i = 0; i < refToObj.Length; i++)
            {
                refToObj[i] = default;
            }

            var objToRef = _objectToReference.AsSpan(0, Math.Min(_objectToReference.Length, _objectToReferenceCount));
            for (var i = 0; i < objToRef.Length; i++)
            {
                objToRef[i] = default;
            }

            ReferenceToObjectCount = 0;
            _objectToReferenceCount = 0;
            CurrentReferenceId = 0;

            _referenceToObjectOverflow = null;
            _objectToReferenceOverflow = null;
        }
    }
}
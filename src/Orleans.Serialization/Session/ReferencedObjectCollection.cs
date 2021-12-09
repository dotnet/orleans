using Orleans.Serialization.Codecs;
using Orleans.Serialization.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Session
{
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

        public int ReferenceToObjectCount { get; set; }
        private readonly ReferencePair[] _referenceToObject = new ReferencePair[64];

        private int _objectToReferenceCount;
        private readonly ReferencePair[] _objectToReference = new ReferencePair[64];

        private Dictionary<uint, object> _referenceToObjectOverflow;
        private Dictionary<object, uint> _objectToReferenceOverflow;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetReferencedObject(uint reference, out object value)
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkValueField() => ++CurrentReferenceId;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordReferenceField(object value) => RecordReferenceField(value, ++CurrentReferenceId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordReferenceField(object value, uint referenceId)
        {
            if (value is null)
            {
                return;
            }

            AddToReferences(value, referenceId);
        }

        public Dictionary<uint, object> CopyReferenceTable() => _referenceToObject.Take(ReferenceToObjectCount).ToDictionary(r => r.Id, r => r.Object);
        public Dictionary<object, uint> CopyIdTable() => _objectToReference.Take(_objectToReferenceCount).ToDictionary(r => r.Object, r => r.Id);

        public uint CurrentReferenceId { get; set; }

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
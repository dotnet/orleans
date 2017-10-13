using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using Orleans.ApplicationParts;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    /// <summary>
    /// Populates a <see cref="SerializerFeature"/> instance with the built-in serializers and known types.
    /// </summary>
    internal class BuiltInTypesSerializationFeaturePopulator : IApplicationFeatureProvider<SerializerFeature>
    {
        /// <inheritdoc />
        public void PopulateFeature(IEnumerable<IApplicationPart> parts, SerializerFeature feature)
        {
            // Built-in handlers: Tuples
            feature.AddSerializerDelegates(typeof(Tuple<>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,,,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,,,,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            feature.AddSerializerDelegates(typeof(Tuple<,,,,,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);

            // Built-in handlers: ValueTuples
            feature.AddSerializerDelegates(typeof(ValueTuple<>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,,,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,,,,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,,,,,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,,,,,,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);
            feature.AddSerializerDelegates(typeof(ValueTuple<,,,,,,,>), BuiltInTypes.DeepCopyValueTuple, BuiltInTypes.SerializeValueTuple, BuiltInTypes.DeserializeValueTuple);

            // Built-in handlers: enumerables
            feature.AddSerializerDelegates(typeof(List<>), BuiltInTypes.CopyGenericList, BuiltInTypes.SerializeGenericList, BuiltInTypes.DeserializeGenericList);
            feature.AddSerializerDelegates(
                    typeof(ReadOnlyCollection<>),
                    BuiltInTypes.CopyGenericReadOnlyCollection,
                    BuiltInTypes.SerializeGenericReadOnlyCollection,
                    BuiltInTypes.DeserializeGenericReadOnlyCollection);
            feature.AddSerializerDelegates(typeof(LinkedList<>), BuiltInTypes.CopyGenericLinkedList, BuiltInTypes.SerializeGenericLinkedList, BuiltInTypes.DeserializeGenericLinkedList);
            feature.AddSerializerDelegates(typeof(HashSet<>), BuiltInTypes.CopyGenericHashSet, BuiltInTypes.SerializeGenericHashSet, BuiltInTypes.DeserializeGenericHashSet);
            feature.AddSerializerDelegates(typeof(SortedSet<>), BuiltInTypes.CopyGenericSortedSet, BuiltInTypes.SerializeGenericSortedSet, BuiltInTypes.DeserializeGenericSortedSet);
            feature.AddSerializerDelegates(typeof(Stack<>), BuiltInTypes.CopyGenericStack, BuiltInTypes.SerializeGenericStack, BuiltInTypes.DeserializeGenericStack);
            feature.AddSerializerDelegates(typeof(Queue<>), BuiltInTypes.CopyGenericQueue, BuiltInTypes.SerializeGenericQueue, BuiltInTypes.DeserializeGenericQueue);

            // Built-in handlers: dictionaries
            feature.AddSerializerDelegates(
                    typeof(ReadOnlyDictionary<,>),
                    BuiltInTypes.CopyGenericReadOnlyDictionary,
                    BuiltInTypes.SerializeGenericReadOnlyDictionary,
                    BuiltInTypes.DeserializeGenericReadOnlyDictionary);
            feature.AddSerializerDelegates(typeof(Dictionary<,>), BuiltInTypes.CopyGenericDictionary, BuiltInTypes.SerializeGenericDictionary, BuiltInTypes.DeserializeGenericDictionary);
            feature.AddSerializerDelegates(
                    typeof(Dictionary<string, object>),
                    BuiltInTypes.CopyStringObjectDictionary,
                    BuiltInTypes.SerializeStringObjectDictionary,
                    BuiltInTypes.DeserializeStringObjectDictionary);
            feature.AddSerializerDelegates(
                    typeof(SortedDictionary<,>),
                    BuiltInTypes.CopyGenericSortedDictionary,
                    BuiltInTypes.SerializeGenericSortedDictionary,
                    BuiltInTypes.DeserializeGenericSortedDictionary);
            feature.AddSerializerDelegates(typeof(SortedList<,>), BuiltInTypes.CopyGenericSortedList, BuiltInTypes.SerializeGenericSortedList, BuiltInTypes.DeserializeGenericSortedList);

            // Built-in handlers: key-value pairs
            feature.AddSerializerDelegates(typeof(KeyValuePair<,>), BuiltInTypes.CopyGenericKeyValuePair, BuiltInTypes.SerializeGenericKeyValuePair, BuiltInTypes.DeserializeGenericKeyValuePair);

            // Built-in handlers: nullables
            feature.AddSerializerDelegates(typeof(Nullable<>), BuiltInTypes.CopyGenericNullable, BuiltInTypes.SerializeGenericNullable, BuiltInTypes.DeserializeGenericNullable);

            // Built-in handlers: Immutables
            feature.AddSerializerDelegates(typeof(Immutable<>), BuiltInTypes.CopyGenericImmutable, BuiltInTypes.SerializeGenericImmutable, BuiltInTypes.DeserializeGenericImmutable);

            // Built-in handlers: Immutable collections
            feature.AddSerializerDelegates(typeof(ImmutableQueue<>), BuiltInTypes.CopyGenericImmutableQueue, BuiltInTypes.SerializeGenericImmutableQueue, BuiltInTypes.DeserializeGenericImmutableQueue);
            feature.AddSerializerDelegates(typeof(ImmutableArray<>), BuiltInTypes.CopyGenericImmutableArray, BuiltInTypes.SerializeGenericImmutableArray, BuiltInTypes.DeserializeGenericImmutableArray);
            feature.AddSerializerDelegates(
                    typeof(ImmutableSortedDictionary<,>),
                    BuiltInTypes.CopyGenericImmutableSortedDictionary,
                    BuiltInTypes.SerializeGenericImmutableSortedDictionary,
                    BuiltInTypes.DeserializeGenericImmutableSortedDictionary);
            feature.AddSerializerDelegates(
                    typeof(ImmutableSortedSet<>),
                    BuiltInTypes.CopyGenericImmutableSortedSet,
                    BuiltInTypes.SerializeGenericImmutableSortedSet,
                    BuiltInTypes.DeserializeGenericImmutableSortedSet);
            feature.AddSerializerDelegates(
                    typeof(ImmutableHashSet<>),
                    BuiltInTypes.CopyGenericImmutableHashSet,
                    BuiltInTypes.SerializeGenericImmutableHashSet,
                    BuiltInTypes.DeserializeGenericImmutableHashSet);
            feature.AddSerializerDelegates(
                    typeof(ImmutableDictionary<,>),
                    BuiltInTypes.CopyGenericImmutableDictionary,
                    BuiltInTypes.SerializeGenericImmutableDictionary,
                    BuiltInTypes.DeserializeGenericImmutableDictionary);
            feature.AddSerializerDelegates(typeof(ImmutableList<>), BuiltInTypes.CopyGenericImmutableList, BuiltInTypes.SerializeGenericImmutableList, BuiltInTypes.DeserializeGenericImmutableList);

            // Built-in handlers: random system types
            feature.AddSerializerDelegates(typeof(TimeSpan), BuiltInTypes.CopyTimeSpan, BuiltInTypes.SerializeTimeSpan, BuiltInTypes.DeserializeTimeSpan);
            feature.AddSerializerDelegates(typeof(DateTimeOffset), BuiltInTypes.CopyDateTimeOffset, BuiltInTypes.SerializeDateTimeOffset, BuiltInTypes.DeserializeDateTimeOffset);
            feature.AddSerializerDelegates(typeof(Guid), BuiltInTypes.CopyGuid, BuiltInTypes.SerializeGuid, BuiltInTypes.DeserializeGuid);
            feature.AddSerializerDelegates(typeof(IPAddress), BuiltInTypes.CopyIPAddress, BuiltInTypes.SerializeIPAddress, BuiltInTypes.DeserializeIPAddress);
            feature.AddSerializerDelegates(typeof(IPEndPoint), BuiltInTypes.CopyIPEndPoint, BuiltInTypes.SerializeIPEndPoint, BuiltInTypes.DeserializeIPEndPoint);
            feature.AddSerializerDelegates(typeof(Uri), BuiltInTypes.CopyUri, BuiltInTypes.SerializeUri, BuiltInTypes.DeserializeUri);
            feature.AddSerializerDelegates(typeof(CultureInfo), BuiltInTypes.CopyCultureInfo, BuiltInTypes.SerializeCultureInfo, BuiltInTypes.DeserializeCultureInfo);

            // Built-in handlers: Orleans internal types
            feature.AddSerializerDelegates(
                    typeof(InvokeMethodRequest),
                    BuiltInTypes.CopyInvokeMethodRequest,
                    BuiltInTypes.SerializeInvokeMethodRequest,
                    BuiltInTypes.DeserializeInvokeMethodRequest);
            feature.AddSerializerDelegates(
                    typeof(Response),
                    BuiltInTypes.CopyOrleansResponse,
                    BuiltInTypes.SerializeOrleansResponse,
                    BuiltInTypes.DeserializeOrleansResponse);
            feature.AddSerializerDelegates(typeof(ActivationId), BuiltInTypes.CopyActivationId, BuiltInTypes.SerializeActivationId, BuiltInTypes.DeserializeActivationId);
            feature.AddSerializerDelegates(typeof(GrainId), BuiltInTypes.CopyGrainId, BuiltInTypes.SerializeGrainId, BuiltInTypes.DeserializeGrainId);
            feature.AddSerializerDelegates(typeof(ActivationAddress), BuiltInTypes.CopyActivationAddress, BuiltInTypes.SerializeActivationAddress, BuiltInTypes.DeserializeActivationAddress);
            feature.AddSerializerDelegates(typeof(CorrelationId), BuiltInTypes.CopyCorrelationId, BuiltInTypes.SerializeCorrelationId, BuiltInTypes.DeserializeCorrelationId);
            feature.AddSerializerDelegates(typeof(SiloAddress), BuiltInTypes.CopySiloAddress, BuiltInTypes.SerializeSiloAddress, BuiltInTypes.DeserializeSiloAddress);

            feature.AddSerializerDelegates(typeof(ExceptionDispatchInfo), (original, context) => original, null, null);

            // Type names that we need to recognize for generic parameters
            feature.AddKnownType(typeof(bool));
            feature.AddKnownType(typeof(int));
            feature.AddKnownType(typeof(short));
            feature.AddKnownType(typeof(sbyte));
            feature.AddKnownType(typeof(long));
            feature.AddKnownType(typeof(uint));
            feature.AddKnownType(typeof(ushort));
            feature.AddKnownType(typeof(byte));
            feature.AddKnownType(typeof(ulong));
            feature.AddKnownType(typeof(float));
            feature.AddKnownType(typeof(double));
            feature.AddKnownType(typeof(decimal));
            feature.AddKnownType(typeof(string));
            feature.AddKnownType(typeof(char));
            feature.AddKnownType(typeof(DateTime));
            feature.AddKnownType(typeof(TimeSpan));
            feature.AddKnownType(typeof(object));
            feature.AddKnownType(typeof(IPAddress));
            feature.AddKnownType(typeof(IPEndPoint));
            feature.AddKnownType(typeof(Guid));

            feature.AddKnownType(typeof(GrainId));
            feature.AddKnownType(typeof(ActivationId));
            feature.AddKnownType(typeof(SiloAddress));
            feature.AddKnownType(typeof(ActivationAddress));
            feature.AddKnownType(typeof(CorrelationId));
            feature.AddKnownType(typeof(InvokeMethodRequest));
            feature.AddKnownType(typeof(Response));

            feature.AddKnownType(typeof(IList<>));
            feature.AddKnownType(typeof(IDictionary<,>));
            feature.AddKnownType(typeof(IEnumerable<>));
            feature.AddKnownType(typeof(ICollection<>));
            feature.AddKnownType(typeof(ISet<>));
            feature.AddKnownType(typeof(IReadOnlyDictionary<,>));
            feature.AddKnownType(typeof(IReadOnlyCollection<>));
            feature.AddKnownType(typeof(IReadOnlyList<>));

            // Enum names we need to recognize
            feature.AddKnownType(typeof(Message.Categories));
            feature.AddKnownType(typeof(Message.Directions));
            feature.AddKnownType(typeof(Message.RejectionTypes));
            feature.AddKnownType(typeof(Message.ResponseTypes));
        }
    }
}
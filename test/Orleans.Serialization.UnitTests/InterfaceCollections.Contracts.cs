using System;
using System.Collections.Generic;
using Orleans.Serialization.Codecs;

namespace Orleans.Serialization.InterfaceCollectionSmoke;

internal static class InterfaceCollectionContracts
{
    internal static IEnumerable<(Type Codec, Type Interface)> SupportedTypes =>
    [
        (typeof(EnumerableCodec<int>), typeof(IEnumerable<int>)),
        (typeof(ReadOnlyCollectionInterfaceCodec<int>), typeof(IReadOnlyCollection<int>)),
        (typeof(ReadOnlyListInterfaceCodec<int>), typeof(IReadOnlyList<int>)),
        (typeof(CollectionInterfaceCodec<int>), typeof(ICollection<int>)),
        (typeof(ListInterfaceCodec<int>), typeof(IList<int>)),
        (typeof(SetInterfaceCodec<int>), typeof(ISet<int>)),
#if NET5_0_OR_GREATER
        (typeof(ReadOnlySetInterfaceCodec<int>), typeof(IReadOnlySet<int>)),
#endif
        (typeof(DictionaryInterfaceCodec<int, long>), typeof(IDictionary<int, long>)),
        (typeof(ReadOnlyDictionaryInterfaceCodec<int, long>), typeof(IReadOnlyDictionary<int, long>)),
        (typeof(EnumerableCodec<string>), typeof(IEnumerable<string>)),
        (typeof(ReadOnlyCollectionInterfaceCodec<string>), typeof(IReadOnlyCollection<string>)),
        (typeof(ReadOnlyListInterfaceCodec<string>), typeof(IReadOnlyList<string>)),
        (typeof(CollectionInterfaceCodec<string>), typeof(ICollection<string>)),
        (typeof(ListInterfaceCodec<string>), typeof(IList<string>)),
        (typeof(SetInterfaceCodec<string>), typeof(ISet<string>)),
#if NET5_0_OR_GREATER
        (typeof(ReadOnlySetInterfaceCodec<string>), typeof(IReadOnlySet<string>)),
#endif
        (typeof(DictionaryInterfaceCodec<string, string>), typeof(IDictionary<string, string>)),
        (typeof(ReadOnlyDictionaryInterfaceCodec<string, string>), typeof(IReadOnlyDictionary<string, string>)),
        (typeof(EnumerableCodec<Dictionary<string, int>>), typeof(IEnumerable<Dictionary<string, int>>)),
        (typeof(ReadOnlyCollectionInterfaceCodec<int[]>), typeof(IReadOnlyCollection<int[]>)),
        (typeof(ReadOnlyListInterfaceCodec<List<int>>), typeof(IReadOnlyList<List<int>>)),
        (typeof(CollectionInterfaceCodec<int?>), typeof(ICollection<int?>)),
        (typeof(ListInterfaceCodec<KeyValuePair<string, int>>), typeof(IList<KeyValuePair<string, int>>)),
        (typeof(SetInterfaceCodec<Guid>), typeof(ISet<Guid>)),
#if NET5_0_OR_GREATER
        (typeof(ReadOnlySetInterfaceCodec<Guid>), typeof(IReadOnlySet<Guid>)),
#endif
        (typeof(DictionaryInterfaceCodec<int, List<string>>), typeof(IDictionary<int, List<string>>)),
        (typeof(ReadOnlyDictionaryInterfaceCodec<string, int?>), typeof(IReadOnlyDictionary<string, int?>))
    ];

    internal static IEnumerable<Type?> UnsupportedTypes =>
    [
        null,
        typeof(int),
        typeof(int[]),
        typeof(IEnumerable<int>),
        typeof(List<int>),
        typeof(Dictionary<int, long>),
        typeof(ListCodec<int>),
        typeof(EnumerableCopier<int>),
        typeof(ListInterfaceCodec<IEnumerable<int>, int>),
        typeof(SetInterfaceCodec<ISet<int>, int>),
        typeof(DictionaryInterfaceCodec<IDictionary<int, long>, int, long>),
        typeof(EnumerableCodec<>),
        typeof(ReadOnlyCollectionInterfaceCodec<>),
        typeof(ReadOnlyListInterfaceCodec<>),
        typeof(CollectionInterfaceCodec<>),
        typeof(ListInterfaceCodec<>),
        typeof(SetInterfaceCodec<>),
#if NET5_0_OR_GREATER
        typeof(ReadOnlySetInterfaceCodec<>),
#endif
        typeof(DictionaryInterfaceCodec<,>),
        typeof(ReadOnlyDictionaryInterfaceCodec<,>)
    ];
}

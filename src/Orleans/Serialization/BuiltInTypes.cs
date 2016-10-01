using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal static class BuiltInTypes
    {
        #region Constants

        private static readonly Type objectType = typeof(object);

        #endregion

        #region Generic collections
        internal static void SerializeGenericReadOnlyCollection(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeReadOnlyCollection), nameof(DeserializeReadOnlyCollection), nameof(DeepCopyReadOnlyCollection), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericReadOnlyCollection(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeReadOnlyCollection), nameof(DeserializeReadOnlyCollection), nameof(DeepCopyReadOnlyCollection), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericReadOnlyCollection(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeReadOnlyCollection), nameof(DeserializeReadOnlyCollection), nameof(DeepCopyReadOnlyCollection), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeReadOnlyCollection<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var collection = (ReadOnlyCollection<T>)obj;
            stream.Write(collection.Count);
            foreach (var element in collection)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeReadOnlyCollection<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var list = new List<T>(count);

            DeserializationContext.Current.RecordObject(list);
            for (var i = 0; i < count; i++)
            {
                list.Add((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }

            var ret = new ReadOnlyCollection<T>(list);
            DeserializationContext.Current.RecordObject(ret);
            return ret;
        }

        internal static object DeepCopyReadOnlyCollection<T>(object original)
        {
            var collection = (ReadOnlyCollection<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return original;
            }

            var innerList = new List<T>(collection.Count);
            innerList.AddRange(collection.Select(element => (T)SerializationManager.DeepCopyInner(element)));

            var retVal = new ReadOnlyCollection<T>(innerList);
            SerializationContext.Current.RecordObject(original, retVal);
            return retVal;
        }

        #region Lists

        internal static void SerializeGenericList(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeList), nameof(DeserializeList), nameof(DeepCopyList), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericList(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeList), nameof(DeserializeList), nameof(DeepCopyList), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericList(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeList), nameof(DeserializeList), nameof(DeepCopyList), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeList<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var list = (List<T>)obj;
            stream.Write(list.Count);
            foreach (var element in list)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeList<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var list = new List<T>(count);
            DeserializationContext.Current.RecordObject(list);

            for (var i = 0; i < count; i++)
            {
                list.Add((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }
            return list;
        }

        internal static object DeepCopyList<T>(object original)
        {
            var list = (List<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new List<T>(list);
            }

            // set the list capacity, to avoid list resizing.
            var retVal = new List<T>(list.Count);
            SerializationContext.Current.RecordObject(original, retVal);
            retVal.AddRange(list.Select(element => (T)SerializationManager.DeepCopyInner(element)));
            return retVal;
        }

        #endregion

        #region LinkedLists

        internal static void SerializeGenericLinkedList(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeLinkedList), nameof(DeserializeLinkedList), nameof(DeepCopyLinkedList), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericLinkedList(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeLinkedList), nameof(DeserializeLinkedList), nameof(DeepCopyLinkedList), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericLinkedList(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeLinkedList), nameof(DeserializeLinkedList), nameof(DeepCopyLinkedList), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeLinkedList<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var list = (LinkedList<T>)obj;
            stream.Write(list.Count);
            foreach (var element in list)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeLinkedList<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var list = new LinkedList<T>();
            DeserializationContext.Current.RecordObject(list);
            for (var i = 0; i < count; i++)
            {
                list.AddLast((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }
            return list;
        }

        internal static object DeepCopyLinkedList<T>(object original)
        {
            var list = (LinkedList<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new LinkedList<T>(list);
            }

            var retVal = new LinkedList<T>();
            SerializationContext.Current.RecordObject(original, retVal);
            foreach (var item in list)
            {
                retVal.AddLast((T)SerializationManager.DeepCopyInner(item));
            }
            return retVal;
        }

        #endregion

        #region HashSets

        internal static void SerializeGenericHashSet(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeHashSet), nameof(DeserializeHashSet), nameof(DeepCopyHashSet), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericHashSet(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeHashSet), nameof(DeserializeHashSet), nameof(DeepCopyHashSet), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericHashSet(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeHashSet), nameof(DeserializeHashSet), nameof(DeepCopyHashSet), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeHashSet<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var set = (HashSet<T>)obj;
            SerializationManager.SerializeInner(set.Comparer.Equals(EqualityComparer<T>.Default) ? null : set.Comparer,
                stream, typeof(IEqualityComparer<T>));
            stream.Write(set.Count);
            foreach (var element in set)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeHashSet<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var comparer =
                (IEqualityComparer<T>)SerializationManager.DeserializeInner(typeof(IEqualityComparer<T>), stream);
            var count = stream.ReadInt();
            var set = new HashSet<T>(comparer);
            DeserializationContext.Current.RecordObject(set);
            for (var i = 0; i < count; i++)
            {
                set.Add((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }
            return set;
        }

        internal static object DeepCopyHashSet<T>(object original)
        {
            var set = (HashSet<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new HashSet<T>(set, set.Comparer);
            }

            var retVal = new HashSet<T>(set.Comparer);
            SerializationContext.Current.RecordObject(original, retVal);
            foreach (var item in set)
            {
                retVal.Add((T)SerializationManager.DeepCopyInner(item));
            }
            return retVal;
        }

        internal static void SerializeGenericSortedSet(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeSortedSet), nameof(DeserializeSortedSet), nameof(DeepCopySortedSet), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericSortedSet(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeSortedSet), nameof(DeserializeSortedSet), nameof(DeepCopySortedSet), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericSortedSet(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeSortedSet), nameof(DeserializeSortedSet), nameof(DeepCopySortedSet), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeSortedSet<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var set = (SortedSet<T>)obj;
            SerializationManager.SerializeInner(set.Comparer.Equals(Comparer<T>.Default) ? null : set.Comparer,
                stream, typeof(IComparer<T>));
            stream.Write(set.Count);
            foreach (var element in set)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeSortedSet<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var comparer =
                (IComparer<T>)SerializationManager.DeserializeInner(typeof(IComparer<T>), stream);
            var count = stream.ReadInt();
            var set = new SortedSet<T>(comparer);
            DeserializationContext.Current.RecordObject(set);
            for (var i = 0; i < count; i++)
            {
                set.Add((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }
            return set;
        }

        internal static object DeepCopySortedSet<T>(object original)
        {
            var set = (SortedSet<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new SortedSet<T>(set, set.Comparer);
            }

            var retVal = new SortedSet<T>(set.Comparer);
            SerializationContext.Current.RecordObject(original, retVal);
            foreach (var item in set)
            {
                retVal.Add((T)SerializationManager.DeepCopyInner(item));
            }
            return retVal;
        }
        #endregion

        #region Queues

        internal static void SerializeGenericQueue(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeQueue), nameof(DeserializeQueue), nameof(DeepCopyQueue), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericQueue(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeQueue), nameof(DeserializeQueue), nameof(DeepCopyQueue), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericQueue(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeQueue), nameof(DeserializeQueue), nameof(DeepCopyQueue), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeQueue<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var queue = (Queue<T>)obj;
            stream.Write(queue.Count);
            foreach (var element in queue)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeQueue<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var queue = new Queue<T>();
            DeserializationContext.Current.RecordObject(queue);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }
            return queue;
        }

        internal static object DeepCopyQueue<T>(object original)
        {
            var queue = (Queue<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new Queue<T>(queue);
            }

            var retVal = new Queue<T>(queue.Count);
            SerializationContext.Current.RecordObject(original, retVal);
            foreach (var item in queue)
            {
                retVal.Enqueue((T)SerializationManager.DeepCopyInner(item));
            }
            return retVal;
        }

        #endregion

        #region Stacks

        internal static void SerializeGenericStack(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeStack), nameof(DeserializeStack), nameof(DeepCopyStack), generics);

            concretes.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericStack(Type expected, BinaryTokenStreamReader stream)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeStack), nameof(DeserializeStack), nameof(DeepCopyStack), generics);

            return concretes.Item2(expected, stream);
        }

        internal static object CopyGenericStack(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeStack), nameof(DeserializeStack), nameof(DeepCopyStack), generics);

            return concretes.Item3(original);
        }

        internal static void SerializeStack<T>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var stack = (Stack<T>)obj;
            stream.Write(stack.Count);
            foreach (var element in stack)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeStack<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var list = new List<T>(count);
            var stack = new Stack<T>(count);
            DeserializationContext.Current.RecordObject(stack);
            for (var i = 0; i < count; i++)
            {
                list.Add((T)SerializationManager.DeserializeInner(typeof(T), stream));
            }

            for (var i = count - 1; i >= 0; i--)
            {
                stack.Push(list[i]);
            }

            return stack;
        }

        internal static object DeepCopyStack<T>(object original)
        {
            var stack = (Stack<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new Stack<T>(stack.Reverse()); // NOTE: Yes, the Reverse really is required
            }

            var retVal = new Stack<T>();
            SerializationContext.Current.RecordObject(original, retVal);
            foreach (var item in stack.Reverse())
            {
                retVal.Push((T)SerializationManager.DeepCopyInner(item));
            }
            return retVal;
        }

        #endregion

        #region Dictionaries

        internal static void SerializeGenericDictionary(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();

            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeDictionary), nameof(DeserializeDictionary), nameof(CopyDictionary));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericDictionary(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeDictionary), nameof(DeserializeDictionary), nameof(CopyDictionary));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericDictionary(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeDictionary), nameof(DeserializeDictionary), nameof(CopyDictionary));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeDictionary<K, V>(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var dict = (Dictionary<K, V>)original;
            SerializationManager.SerializeInner(dict.Comparer.Equals(EqualityComparer<K>.Default) ? null : dict.Comparer,
                                           stream, typeof(IEqualityComparer<K>));
            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, stream, typeof(K));
                SerializationManager.SerializeInner(pair.Value, stream, typeof(V));
            }
        }

        internal static object DeserializeDictionary<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var comparer = (IEqualityComparer<K>)SerializationManager.DeserializeInner(typeof(IEqualityComparer<K>), stream);
            var count = stream.ReadInt();
            var dict = new Dictionary<K, V>(count, comparer);
            DeserializationContext.Current.RecordObject(dict);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), stream);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), stream);
                dict[key] = value;
            }
            return dict;
        }

        internal static object CopyDictionary<K, V>(object original)
        {
            var dict = (Dictionary<K, V>)original;
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return new Dictionary<K, V>(dict, dict.Comparer);
            }

            var result = new Dictionary<K, V>(dict.Count, dict.Comparer);
            SerializationContext.Current.RecordObject(original, result);
            foreach (var pair in dict)
            {
                result[(K)SerializationManager.DeepCopyInner(pair.Key)] = (V)SerializationManager.DeepCopyInner(pair.Value);
            }

            return result;
        }

        internal static void SerializeGenericReadOnlyDictionary(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeReadOnlyDictionary), nameof(DeserializeReadOnlyDictionary), nameof(CopyReadOnlyDictionary));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericReadOnlyDictionary(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeReadOnlyDictionary), nameof(DeserializeReadOnlyDictionary), nameof(CopyReadOnlyDictionary));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericReadOnlyDictionary(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeReadOnlyDictionary), nameof(DeserializeReadOnlyDictionary), nameof(CopyReadOnlyDictionary));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeReadOnlyDictionary<K, V>(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var dict = (ReadOnlyDictionary<K, V>)original;
            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, stream, typeof(K));
                SerializationManager.SerializeInner(pair.Value, stream, typeof(V));
            }
        }

        internal static object DeserializeReadOnlyDictionary<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var dict = new Dictionary<K, V>(count);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), stream);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), stream);
                dict[key] = value;
            }

            var retVal = new ReadOnlyDictionary<K, V>(dict);
            DeserializationContext.Current.RecordObject(retVal);
            return retVal;
        }

        internal static object CopyReadOnlyDictionary<K, V>(object original)
        {
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return original;
            }

            var dict = (ReadOnlyDictionary<K, V>)original;
            var innerDict = new Dictionary<K, V>(dict.Count);
            foreach (var pair in dict)
            {
                innerDict[(K)SerializationManager.DeepCopyInner(pair.Key)] = (V)SerializationManager.DeepCopyInner(pair.Value);
            }

            var retVal = new ReadOnlyDictionary<K, V>(innerDict);
            SerializationContext.Current.RecordObject(original, retVal);
            return retVal;
        }

        internal static void SerializeStringObjectDictionary(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var dict = (Dictionary<string, object>)original;
            SerializationManager.SerializeInner(dict.Comparer.Equals(EqualityComparer<string>.Default) ? null : dict.Comparer,
                                           stream, typeof(IEqualityComparer<string>));
            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                //stream.WriteTypeHeader(stringType, stringType);
                stream.Write(pair.Key);
                SerializationManager.SerializeInner(pair.Value, stream, objectType);
            }
        }

        internal static object DeserializeStringObjectDictionary(Type expected, BinaryTokenStreamReader stream)
        {
            var comparer = (IEqualityComparer<string>)SerializationManager.DeserializeInner(typeof(IEqualityComparer<string>), stream);
            var count = stream.ReadInt();
            var dict = new Dictionary<string, object>(count, comparer);
            DeserializationContext.Current.RecordObject(dict);
            for (var i = 0; i < count; i++)
            {
                //stream.ReadFullTypeHeader(stringType); // Skip the type header, which will be string
                var key = stream.ReadString();
                var value = SerializationManager.DeserializeInner(null, stream);
                dict[key] = value;
            }
            return dict;
        }

        internal static object CopyStringObjectDictionary(object original)
        {
            var dict = (Dictionary<string, object>)original;
            var result = new Dictionary<string, object>(dict.Count, dict.Comparer);
            SerializationContext.Current.RecordObject(original, result);
            foreach (var pair in dict)
            {
                result[pair.Key] = SerializationManager.DeepCopyInner(pair.Value);
            }

            return result;
        }

        #endregion

        #region SortedDictionaries

        internal static void SerializeGenericSortedDictionary(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedDictionary), nameof(DeserializeSortedDictionary), nameof(CopySortedDictionary));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericSortedDictionary(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeSortedDictionary), nameof(DeserializeSortedDictionary), nameof(CopySortedDictionary));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericSortedDictionary(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedDictionary), nameof(DeserializeSortedDictionary), nameof(CopySortedDictionary));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeSortedDictionary<K, V>(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var dict = (SortedDictionary<K, V>)original;
            SerializationManager.SerializeInner(dict.Comparer.Equals(Comparer<K>.Default) ? null : dict.Comparer, stream, typeof(IComparer<K>));
            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, stream, typeof(K));
                SerializationManager.SerializeInner(pair.Value, stream, typeof(V));
            }
        }

        internal static object DeserializeSortedDictionary<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var comparer = (IComparer<K>)SerializationManager.DeserializeInner(typeof(IComparer<K>), stream);
            var count = stream.ReadInt();
            var dict = new SortedDictionary<K, V>(comparer);
            DeserializationContext.Current.RecordObject(dict);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), stream);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), stream);
                dict[key] = value;
            }
            return dict;
        }

        internal static object CopySortedDictionary<K, V>(object original)
        {
            var dict = (SortedDictionary<K, V>)original;
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return new SortedDictionary<K, V>(dict, dict.Comparer);
            }

            var result = new SortedDictionary<K, V>(dict.Comparer);
            SerializationContext.Current.RecordObject(original, result);
            foreach (var pair in dict)
            {
                result[(K)SerializationManager.DeepCopyInner(pair.Key)] = (V)SerializationManager.DeepCopyInner(pair.Value);
            }

            return result;
        }

        #endregion

        #region SortedLists

        internal static void SerializeGenericSortedList(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedList), nameof(DeserializeSortedList), nameof(CopySortedList));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericSortedList(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeSortedList), nameof(DeserializeSortedList), nameof(CopySortedList));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericSortedList(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedList), nameof(DeserializeSortedList), nameof(CopySortedList));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeSortedList<K, V>(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var list = (SortedList<K, V>)original;
            SerializationManager.SerializeInner(list.Comparer.Equals(Comparer<K>.Default) ? null : list.Comparer, stream, typeof(IComparer<K>));
            stream.Write(list.Count);
            foreach (var pair in list)
            {
                SerializationManager.SerializeInner(pair.Key, stream, typeof(K));
                SerializationManager.SerializeInner(pair.Value, stream, typeof(V));
            }
        }

        internal static object DeserializeSortedList<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var comparer = (IComparer<K>)SerializationManager.DeserializeInner(typeof(IComparer<K>), stream);
            var count = stream.ReadInt();
            var list = new SortedList<K, V>(count, comparer);
            DeserializationContext.Current.RecordObject(list);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), stream);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), stream);
                list[key] = value;
            }
            return list;
        }

        internal static object CopySortedList<K, V>(object original)
        {
            var list = (SortedList<K, V>)original;
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return new SortedList<K, V>(list, list.Comparer);
            }

            var result = new SortedList<K, V>(list.Count, list.Comparer);
            SerializationContext.Current.RecordObject(original, result);
            foreach (var pair in list)
            {
                result[(K)SerializationManager.DeepCopyInner(pair.Key)] = (V)SerializationManager.DeepCopyInner(pair.Value);
            }

            return result;
        }

        #endregion

        #endregion

        #region Immutable Collections

        internal static void SerializeGenericImmutableDictionary(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableDictionary), nameof(DeserializeImmutableDictionary), nameof(CopyImmutableDictionary));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericImmutableDictionary(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableDictionary), nameof(DeserializeImmutableDictionary), nameof(CopyImmutableDictionary));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericImmutableDictionary(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableDictionary), nameof(DeserializeImmutableDictionary), nameof(CopyImmutableDictionary));
            return concreteMethods.Item3(original);
        }

        internal static object CopyImmutableDictionary<K, V>(object original)
        {
            return original;
        }

        internal static void SerializeImmutableDictionary<K, V>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var dict = (ImmutableDictionary<K, V>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(EqualityComparer<K>.Default) ? null : dict.KeyComparer, stream, typeof(IEqualityComparer<K>));
            SerializationManager.SerializeInner(dict.ValueComparer.Equals(EqualityComparer<V>.Default) ? null : dict.ValueComparer, stream, typeof(IEqualityComparer<V>));

            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, stream, typeof(K));
                SerializationManager.SerializeInner(pair.Value, stream, typeof(V));
            }
        }

        internal static object DeserializeImmutableDictionary<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var keyComparer = SerializationManager.DeserializeInner<IEqualityComparer<K>>(stream);
            var valueComparer = SerializationManager.DeserializeInner<IEqualityComparer<V>>(stream);
            var count = stream.ReadInt();
            var dictBuilder = ImmutableDictionary.CreateBuilder(keyComparer, valueComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(stream);
                var value = SerializationManager.DeserializeInner<V>(stream);
                dictBuilder.Add(key, value);
            }
            var dict = dictBuilder.ToImmutable();
            DeserializationContext.Current.RecordObject(dict);

            return dict;
        }

        internal static void SerializeGenericImmutableList(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableList), nameof(DeserializeImmutableList), nameof(CopyImmutableList));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericImmutableList(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableList), nameof(DeserializeImmutableList), nameof(CopyImmutableList));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericImmutableList(object original)
        {
            var t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableList), nameof(DeserializeImmutableList), nameof(CopyImmutableList));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeImmutableList<T>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var list = (ImmutableList<T>)untypedInput;
            stream.Write(list.Count);
            foreach (var element in list)
            {
                SerializationManager.SerializeInner(element, stream, typeof(T));
            }
        }

        internal static object DeserializeImmutableList<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var listBuilder = ImmutableList.CreateBuilder<T>();

            for (var i = 0; i < count; i++)
            {
                listBuilder.Add(SerializationManager.DeserializeInner<T>(stream));
            }
            var list = listBuilder.ToImmutable();
            DeserializationContext.Current.RecordObject(list);
            return list;
        }

        internal static object CopyImmutableList<K>(object original)
        {
            return original;
        }

        internal static object CopyGenericImmutableHashSet(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableHashSet), nameof(DeserializeImmutableHashSet), nameof(CopyImmutableHashSet));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeGenericImmutableHashSet(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableHashSet), nameof(DeserializeImmutableHashSet), nameof(CopyImmutableHashSet));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericImmutableHashSet(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableHashSet), nameof(DeserializeImmutableHashSet), nameof(CopyImmutableHashSet));
            return concreteMethods.Item2(expected, stream);
        }
        
        internal static object CopyImmutableHashSet<K>(object original)
        {
            return original;
        }

        internal static void SerializeImmutableHashSet<K>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var dict = (ImmutableHashSet<K>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(EqualityComparer<K>.Default) ? null : dict.KeyComparer, stream, typeof(IEqualityComparer<K>));

            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair, stream, typeof(K));
            }
        }

        internal static object DeserializeImmutableHashSet<K>(Type expected, BinaryTokenStreamReader stream)
        {
            var keyComparer = SerializationManager.DeserializeInner<IEqualityComparer<K>>(stream);
            var count = stream.ReadInt();
            var dictBuilder = ImmutableHashSet.CreateBuilder(keyComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(stream);
                dictBuilder.Add(key);
            }
            var dict = dictBuilder.ToImmutable();
            DeserializationContext.Current.RecordObject(dict);

            return dict;
        }

        internal static object CopyGenericImmutableSortedSet(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedSet), nameof(DeserializeImmutableSortedSet), nameof(CopyImmutableSortedSet));
            return concreteMethods.Item3(original);
        }

        internal static object DeserializeGenericImmutableSortedSet(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableSortedSet), nameof(DeserializeImmutableSortedSet), nameof(CopyImmutableSortedSet));
            return concreteMethods.Item2(expected, stream);
        }

        internal static void SerializeGenericImmutableSortedSet(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedSet), nameof(DeserializeImmutableSortedSet), nameof(CopyImmutableSortedSet));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object CopyImmutableSortedSet<K>(object original)
        {
            return original;
        }

        internal static void SerializeImmutableSortedSet<K>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var dict = (ImmutableSortedSet<K>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(Comparer<K>.Default) ? null : dict.KeyComparer, stream, typeof(IComparer<K>));

            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair, stream, typeof(K));
            }
        }

        internal static object DeserializeImmutableSortedSet<K>(Type expected, BinaryTokenStreamReader stream)
        {
            var keyComparer = SerializationManager.DeserializeInner<IComparer<K>>(stream);
            var count = stream.ReadInt();
            var dictBuilder = ImmutableSortedSet.CreateBuilder(keyComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(stream);
                dictBuilder.Add(key);
            }
            var dict = dictBuilder.ToImmutable();
            DeserializationContext.Current.RecordObject(dict);

            return dict;
        }

        internal static object CopyGenericImmutableSortedDictionary(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedDictionary), nameof(DeserializeImmutableSortedDictionary), nameof(CopyImmutableSortedDictionary));
            return concreteMethods.Item3(original);
        }

        internal static object DeserializeGenericImmutableSortedDictionary(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableSortedDictionary), nameof(DeserializeImmutableSortedDictionary), nameof(CopyImmutableSortedDictionary));
            return concreteMethods.Item2(expected, stream);
        }

        internal static void SerializeGenericImmutableSortedDictionary(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedDictionary), nameof(DeserializeImmutableSortedDictionary), nameof(CopyImmutableSortedDictionary));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object CopyImmutableSortedDictionary<K, V>(object original)
        {
            return original;
        }

        internal static void SerializeImmutableSortedDictionary<K, V>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var dict = (ImmutableSortedDictionary<K, V>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(Comparer<K>.Default) ? null : dict.KeyComparer, stream, typeof(IComparer<K>));
            SerializationManager.SerializeInner(dict.ValueComparer.Equals(EqualityComparer<V>.Default) ? null : dict.ValueComparer, stream, typeof(IEqualityComparer<V>));

            stream.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, stream, typeof(K));
                SerializationManager.SerializeInner(pair.Value, stream, typeof(V));
            }
        }

        internal static object DeserializeImmutableSortedDictionary<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var keyComparer = SerializationManager.DeserializeInner<IComparer<K>>(stream);
            var valueComparer = SerializationManager.DeserializeInner<IEqualityComparer<V>>(stream);
            var count = stream.ReadInt();
            var dictBuilder = ImmutableSortedDictionary.CreateBuilder(keyComparer, valueComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(stream);
                var value = SerializationManager.DeserializeInner<V>(stream);
                dictBuilder.Add(key, value);
            }
            var dict = dictBuilder.ToImmutable();
            DeserializationContext.Current.RecordObject(dict);

            return dict;
        }

        internal static object CopyGenericImmutableArray(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableArray), nameof(DeserializeImmutableArray), nameof(CopyImmutableArray));
            return concreteMethods.Item3(original);
        }

        internal static object DeserializeGenericImmutableArray(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableArray), nameof(DeserializeImmutableArray), nameof(CopyImmutableArray));
            return concreteMethods.Item2(expected, stream);
        }

        internal static void SerializeGenericImmutableArray(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableArray), nameof(DeserializeImmutableArray), nameof(CopyImmutableArray));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object CopyImmutableArray<K>(object original)
        {
            return original;
        }

        internal static void SerializeImmutableArray<K>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var dict = (ImmutableArray<K>)untypedInput;

            stream.Write(dict.Length);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair, stream, typeof(K));
            }
        }

        internal static object DeserializeImmutableArray<K>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var dictBuilder = ImmutableArray.CreateBuilder<K>();
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(stream);
                dictBuilder.Add(key);
            }
            var dict = dictBuilder.ToImmutable();
            DeserializationContext.Current.RecordObject(dict);

            return dict;
        }
        internal static object CopyGenericImmutableQueue(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableQueue), nameof(DeserializeImmutableQueue), nameof(CopyImmutableQueue));
            return concreteMethods.Item3(original);
        }

        internal static object DeserializeGenericImmutableQueue(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableQueue), nameof(DeserializeImmutableQueue), nameof(CopyImmutableQueue));
            return concreteMethods.Item2(expected, stream);
        }

        internal static void SerializeGenericImmutableQueue(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableQueue), nameof(DeserializeImmutableQueue), nameof(CopyImmutableQueue));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object CopyImmutableQueue<K>(object original)
        {
            return original;
        }

        internal static void SerializeImmutableQueue<K>(object untypedInput, BinaryTokenStreamWriter stream, Type typeExpected)
        {
            var queue = (ImmutableQueue<K>)untypedInput;

            stream.Write(queue.Count());
            foreach (var item in queue)
            {
                SerializationManager.SerializeInner(item, stream, typeof(K));
            }
        }

        internal static object DeserializeImmutableQueue<K>(Type expected, BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var items = new K[count];
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(stream);
                items[i] = key;
            }
            var queues = ImmutableQueue.CreateRange(items);

            DeserializationContext.Current.RecordObject(queues);

            return queues;
        }
        #endregion

        #region Other generics

        #region Tuples

        internal static void SerializeTuple(object raw, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = raw.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeTuple) + generics.Length, nameof(DeserializeTuple) + generics.Length, nameof(DeepCopyTuple) + generics.Length, generics);

            concretes.Item1(raw, stream, expected);
        }

        internal static object DeserializeTuple(Type t, BinaryTokenStreamReader stream)
        {
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeTuple) + generics.Length, nameof(DeserializeTuple) + generics.Length, nameof(DeepCopyTuple) + generics.Length, generics);

            return concretes.Item2(t, stream);
        }

        internal static object DeepCopyTuple(object original)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeTuple) + generics.Length, nameof(DeserializeTuple) + generics.Length, nameof(DeepCopyTuple) + generics.Length, generics);

            return concretes.Item3(original);
        }

        internal static object DeepCopyTuple1<T1>(object original)
        {
            var input = (Tuple<T1>)original;
            var result = new Tuple<T1>((T1)SerializationManager.DeepCopyInner(input.Item1));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple1<T1>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
        }

        internal static object DeserializeTuple1<T1>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            return new Tuple<T1>(item1);
        }

        internal static object DeepCopyTuple2<T1, T2>(object original)
        {
            var input = (Tuple<T1, T2>)original;
            var result = new Tuple<T1, T2>((T1)SerializationManager.DeepCopyInner(input.Item1), (T2)SerializationManager.DeepCopyInner(input.Item2));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple2<T1, T2>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1, T2>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, stream, typeof(T2));
        }

        internal static object DeserializeTuple2<T1, T2>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), stream);
            return new Tuple<T1, T2>(item1, item2);
        }

        internal static object DeepCopyTuple3<T1, T2, T3>(object original)
        {
            var input = (Tuple<T1, T2, T3>)original;
            var result = new Tuple<T1, T2, T3>((T1)SerializationManager.DeepCopyInner(input.Item1), (T2)SerializationManager.DeepCopyInner(input.Item2),
                (T3)SerializationManager.DeepCopyInner(input.Item3));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple3<T1, T2, T3>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1, T2, T3>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, stream, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, stream, typeof(T3));
        }

        internal static object DeserializeTuple3<T1, T2, T3>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), stream);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), stream);
            return new Tuple<T1, T2, T3>(item1, item2, item3);
        }

        internal static object DeepCopyTuple4<T1, T2, T3, T4>(object original)
        {
            var input = (Tuple<T1, T2, T3, T4>)original;
            var result = new Tuple<T1, T2, T3, T4>((T1)SerializationManager.DeepCopyInner(input.Item1), (T2)SerializationManager.DeepCopyInner(input.Item2),
                (T3)SerializationManager.DeepCopyInner(input.Item3),
                (T4)SerializationManager.DeepCopyInner(input.Item4));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple4<T1, T2, T3, T4>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, stream, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, stream, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, stream, typeof(T4));
        }

        internal static object DeserializeTuple4<T1, T2, T3, T4>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), stream);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), stream);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), stream);
            return new Tuple<T1, T2, T3, T4>(item1, item2, item3, item4);
        }

        internal static object DeepCopyTuple5<T1, T2, T3, T4, T5>(object original)
        {
            var input = (Tuple<T1, T2, T3, T4, T5>)original;
            var result = new Tuple<T1, T2, T3, T4, T5>((T1)SerializationManager.DeepCopyInner(input.Item1), (T2)SerializationManager.DeepCopyInner(input.Item2),
                (T3)SerializationManager.DeepCopyInner(input.Item3),
                (T4)SerializationManager.DeepCopyInner(input.Item4),
                (T5)SerializationManager.DeepCopyInner(input.Item5));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple5<T1, T2, T3, T4, T5>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4, T5>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, stream, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, stream, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, stream, typeof(T4));
            SerializationManager.SerializeInner(input.Item5, stream, typeof(T5));
        }

        internal static object DeserializeTuple5<T1, T2, T3, T4, T5>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), stream);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), stream);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), stream);
            var item5 = (T5)SerializationManager.DeserializeInner(typeof(T5), stream);
            return new Tuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
        }

        internal static object DeepCopyTuple6<T1, T2, T3, T4, T5, T6>(object original)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6>)original;
            var result = new Tuple<T1, T2, T3, T4, T5, T6>((T1)SerializationManager.DeepCopyInner(input.Item1), (T2)SerializationManager.DeepCopyInner(input.Item2),
                (T3)SerializationManager.DeepCopyInner(input.Item3),
                (T4)SerializationManager.DeepCopyInner(input.Item4),
                (T5)SerializationManager.DeepCopyInner(input.Item5),
                (T6)SerializationManager.DeepCopyInner(input.Item6));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple6<T1, T2, T3, T4, T5, T6>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, stream, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, stream, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, stream, typeof(T4));
            SerializationManager.SerializeInner(input.Item5, stream, typeof(T5));
            SerializationManager.SerializeInner(input.Item6, stream, typeof(T6));
        }

        internal static object DeserializeTuple6<T1, T2, T3, T4, T5, T6>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), stream);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), stream);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), stream);
            var item5 = (T5)SerializationManager.DeserializeInner(typeof(T5), stream);
            var item6 = (T6)SerializationManager.DeserializeInner(typeof(T6), stream);
            return new Tuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
        }

        internal static object DeepCopyTuple7<T1, T2, T3, T4, T5, T6, T7>(object original)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6, T7>)original;
            var result = new Tuple<T1, T2, T3, T4, T5, T6, T7>((T1)SerializationManager.DeepCopyInner(input.Item1), (T2)SerializationManager.DeepCopyInner(input.Item2),
                (T3)SerializationManager.DeepCopyInner(input.Item3),
                (T4)SerializationManager.DeepCopyInner(input.Item4),
                (T5)SerializationManager.DeepCopyInner(input.Item5),
                (T6)SerializationManager.DeepCopyInner(input.Item6),
                (T7)SerializationManager.DeepCopyInner(input.Item7));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        internal static void SerializeTuple7<T1, T2, T3, T4, T5, T6, T7>(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6, T7>)obj;
            SerializationManager.SerializeInner(input.Item1, stream, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, stream, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, stream, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, stream, typeof(T4));
            SerializationManager.SerializeInner(input.Item5, stream, typeof(T5));
            SerializationManager.SerializeInner(input.Item6, stream, typeof(T6));
            SerializationManager.SerializeInner(input.Item7, stream, typeof(T7));
        }

        internal static object DeserializeTuple7<T1, T2, T3, T4, T5, T6, T7>(Type expected, BinaryTokenStreamReader stream)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), stream);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), stream);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), stream);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), stream);
            var item5 = (T5)SerializationManager.DeserializeInner(typeof(T5), stream);
            var item6 = (T6)SerializationManager.DeserializeInner(typeof(T6), stream);
            var item7 = (T7)SerializationManager.DeserializeInner(typeof(T7), stream);
            return new Tuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
        }

        #endregion

        #region KeyValuePairs

        internal static void SerializeGenericKeyValuePair(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeKeyValuePair), nameof(DeserializeKeyValuePair), nameof(CopyKeyValuePair));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericKeyValuePair(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeKeyValuePair), nameof(DeserializeKeyValuePair), nameof(CopyKeyValuePair));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericKeyValuePair(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeKeyValuePair), nameof(DeserializeKeyValuePair), nameof(CopyKeyValuePair));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeKeyValuePair<TK, TV>(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var pair = (KeyValuePair<TK, TV>)original;
            SerializationManager.SerializeInner(pair.Key, stream, typeof(TK));
            SerializationManager.SerializeInner(pair.Value, stream, typeof(TV));
        }

        internal static object DeserializeKeyValuePair<K, V>(Type expected, BinaryTokenStreamReader stream)
        {
            var key = (K)SerializationManager.DeserializeInner(typeof(K), stream);
            var value = (V)SerializationManager.DeserializeInner(typeof(V), stream);
            return new KeyValuePair<K, V>(key, value);
        }

        internal static object CopyKeyValuePair<TK, TV>(object original)
        {
            var pair = (KeyValuePair<TK, TV>)original;
            if (typeof(TK).IsOrleansShallowCopyable() && typeof(TV).IsOrleansShallowCopyable())
            {
                return pair;    // KeyValuePair is a struct, so there's already been a copy at this point
            }

            var result = new KeyValuePair<TK, TV>((TK)SerializationManager.DeepCopyInner(pair.Key), (TV)SerializationManager.DeepCopyInner(pair.Value));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        #endregion

        #region Nullables

        internal static void SerializeGenericNullable(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeNullable), nameof(DeserializeNullable), nameof(CopyNullable));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericNullable(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeNullable), nameof(DeserializeNullable), nameof(CopyNullable));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericNullable(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeNullable), nameof(DeserializeNullable), nameof(CopyNullable));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeNullable<T>(object original, BinaryTokenStreamWriter stream, Type expected) where T : struct
        {
            var obj = (T?)original;
            if (obj.HasValue)
            {
                SerializationManager.SerializeInner(obj.Value, stream, typeof(T));
            }
            else
            {
                stream.WriteNull();
            }
        }

        internal static object DeserializeNullable<T>(Type expected, BinaryTokenStreamReader stream) where T : struct
        {
            if (stream.PeekToken() == SerializationTokenType.Null)
            {
                stream.ReadToken();
                return new T?();
            }

            var val = (T)SerializationManager.DeserializeInner(typeof(T), stream);
            return new Nullable<T>(val);
        }

        internal static object CopyNullable<T>(object original) where T : struct
        {
            return original;    // Everything is a struct, so a direct copy is fine
        }

        #endregion

        #region Immutables

        internal static void SerializeGenericImmutable(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutable), nameof(DeserializeImmutable), nameof(CopyImmutable));
            concreteMethods.Item1(original, stream, expected);
        }

        internal static object DeserializeGenericImmutable(Type expected, BinaryTokenStreamReader stream)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutable), nameof(DeserializeImmutable), nameof(CopyImmutable));
            return concreteMethods.Item2(expected, stream);
        }

        internal static object CopyGenericImmutable(object original)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutable), nameof(DeserializeImmutable), nameof(CopyImmutable));
            return concreteMethods.Item3(original);
        }

        internal static void SerializeImmutable<T>(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            var obj = (Immutable<T>)original;
            SerializationManager.SerializeInner(obj.Value, stream, typeof(T));
        }

        internal static object DeserializeImmutable<T>(Type expected, BinaryTokenStreamReader stream)
        {
            var val = (T)SerializationManager.DeserializeInner(typeof(T), stream);
            return new Immutable<T>(val);
        }

        internal static object CopyImmutable<T>(object original)
        {
            return original;    // Immutable means never having to make a copy...
        }

        #endregion

        #endregion

        #region Other System types

        #region TimeSpan

        internal static void SerializeTimeSpan(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var ts = (TimeSpan)obj;
            stream.Write(ts.Ticks);
        }

        internal static object DeserializeTimeSpan(Type expected, BinaryTokenStreamReader stream)
        {
            return new TimeSpan(stream.ReadLong());
        }

        internal static object CopyTimeSpan(object obj)
        {
            return obj; // TimeSpan is a value type 
        }

        #endregion

        #region DateTimeOffset

        internal static void SerializeDateTimeOffset(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var dts = (DateTimeOffset)obj;
            stream.Write(dts.DateTime.Ticks);
            stream.Write(dts.Offset.Ticks);
        }

        internal static object DeserializeDateTimeOffset(Type expected, BinaryTokenStreamReader stream)
        {
            return new DateTimeOffset(stream.ReadLong(), new TimeSpan(stream.ReadLong()));
        }

        internal static object CopyDateTimeOffset(object obj)
        {
            return obj; // DateTimeOffset is a value type 
        }

        #endregion

        #region Type

        internal static void SerializeType(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            stream.Write(((Type)obj).OrleansTypeName());
        }

        internal static object DeserializeType(Type expected, BinaryTokenStreamReader stream)
        {
            return SerializationManager.ResolveTypeName(stream.ReadString());
        }

        internal static object CopyType(object obj)
        {
            return obj; // Type objects are effectively immutable
        }

        #endregion Type

        #region GUID

        internal static void SerializeGuid(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var guid = (Guid)obj;
            stream.Write(guid.ToByteArray());
        }

        internal static object DeserializeGuid(Type expected, BinaryTokenStreamReader stream)
        {
            var bytes = stream.ReadBytes(16);
            return new Guid(bytes);
        }

        internal static object CopyGuid(object obj)
        {
            return obj; // Guids are value types
        }

        #endregion

        #region URIs

        [ThreadStatic]
        static private TypeConverter uriConverter;

        internal static void SerializeUri(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            if (uriConverter == null) uriConverter = TypeDescriptor.GetConverter(typeof(Uri));
            stream.Write(uriConverter.ConvertToInvariantString(obj));
        }

        internal static object DeserializeUri(Type expected, BinaryTokenStreamReader stream)
        {
            if (uriConverter == null) uriConverter = TypeDescriptor.GetConverter(typeof(Uri));
            return uriConverter.ConvertFromInvariantString(stream.ReadString());
        }

        internal static object CopyUri(object obj)
        {
            return obj; // URIs are immutable
        }

        #endregion

        #endregion

        #region Internal Orleans types

        #region Basic types

        internal static void SerializeGrainId(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var id = (GrainId)obj;
            stream.Write(id);
        }

        internal static object DeserializeGrainId(Type expected, BinaryTokenStreamReader stream)
        {
            return stream.ReadGrainId();
        }

        internal static object CopyGrainId(object original)
        {
            return original;
        }

        internal static void SerializeActivationId(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var id = (ActivationId)obj;
            stream.Write(id);
        }

        internal static object DeserializeActivationId(Type expected, BinaryTokenStreamReader stream)
        {
            return stream.ReadActivationId();
        }

        internal static object CopyActivationId(object original)
        {
            return original;
        }

        internal static void SerializeActivationAddress(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var addr = (ActivationAddress)obj;
            stream.Write(addr);
        }

        internal static object DeserializeActivationAddress(Type expected, BinaryTokenStreamReader stream)
        {
            return stream.ReadActivationAddress();
        }

        internal static object CopyActivationAddress(object original)
        {
            return original;
        }

        internal static void SerializeIPAddress(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var ip = (IPAddress)obj;
            stream.Write(ip);
        }

        internal static object DeserializeIPAddress(Type expected, BinaryTokenStreamReader stream)
        {
            return stream.ReadIPAddress();
        }

        internal static object CopyIPAddress(object original)
        {
            return original;
        }

        internal static void SerializeIPEndPoint(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var ep = (IPEndPoint)obj;
            stream.Write(ep);
        }

        internal static object DeserializeIPEndPoint(Type expected, BinaryTokenStreamReader stream)
        {
            return stream.ReadIPEndPoint();
        }

        internal static object CopyIPEndPoint(object original)
        {
            return original;
        }

        internal static void SerializeCorrelationId(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var id = (CorrelationId)obj;
            stream.Write(id);
        }

        internal static object DeserializeCorrelationId(Type expected, BinaryTokenStreamReader stream)
        {
            var bytes = stream.ReadBytes(CorrelationId.SIZE_BYTES);
            return new CorrelationId(bytes);
        }

        internal static object CopyCorrelationId(object original)
        {
            return original;
        }

        internal static void SerializeSiloAddress(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var addr = (SiloAddress)obj;
            stream.Write(addr);
        }

        internal static object DeserializeSiloAddress(Type expected, BinaryTokenStreamReader stream)
        {
            return stream.ReadSiloAddress();
        }

        internal static object CopySiloAddress(object original)
        {
            return original;
        }

        internal static object CopyTaskId(object original)
        {
            return original;
        }

        #endregion

        #region InvokeMethodRequest

        internal static void SerializeInvokeMethodRequest(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var request = (InvokeMethodRequest)obj;

            stream.Write(request.InterfaceId);
            stream.Write(request.MethodId);
            stream.Write(request.Arguments != null ? request.Arguments.Length : 0);
            if (request.Arguments != null)
            {
                foreach (var arg in request.Arguments)
                {
                    SerializationManager.SerializeInner(arg, stream, null);
                }
            }
        }

        internal static object DeserializeInvokeMethodRequest(Type expected, BinaryTokenStreamReader stream)
        {
            int iid = stream.ReadInt();
            int mid = stream.ReadInt();

            int argCount = stream.ReadInt();
            object[] args = null;

            if (argCount > 0)
            {
                args = new object[argCount];
                for (var i = 0; i < argCount; i++)
                {
                    args[i] = SerializationManager.DeserializeInner(null, stream);
                }
            }

            return new InvokeMethodRequest(iid, mid, args);
        }

        internal static object CopyInvokeMethodRequest(object original)
        {
            var request = (InvokeMethodRequest)original;

            object[] args = null;
            if (request.Arguments != null)
            {
                args = new object[request.Arguments.Length];
                for (var i = 0; i < request.Arguments.Length; i++)
                {
                    args[i] = SerializationManager.DeepCopyInner(request.Arguments[i]);
                }
            }

            var result = new InvokeMethodRequest(request.InterfaceId, request.MethodId, args);
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        #endregion

        #region Response

        internal static void SerializeOrleansResponse(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var resp = (Response)obj;

            SerializationManager.SerializeInner(resp.ExceptionFlag ? resp.Exception : resp.Data, stream, null);
        }

        internal static object DeserializeOrleansResponse(Type expected, BinaryTokenStreamReader stream)
        {
            var obj = SerializationManager.DeserializeInner(null, stream);
            return new Response(obj);
        }

        internal static object CopyOrleansResponse(object original)
        {
            var resp = (Response)original;

            if (resp.ExceptionFlag)
            {
                return original;
            }

            var result = new Response(SerializationManager.DeepCopyInner(resp.Data));
            SerializationContext.Current.RecordObject(original, result);
            return result;
        }

        #endregion

        #endregion

        #region Utilities

        private static Tuple<SerializationManager.Serializer, SerializationManager.Deserializer, SerializationManager.DeepCopier>
            RegisterConcreteMethods(Type t, string serializerName, string deserializerName, string copierName, Type[] genericArgs = null)
        {
            if (genericArgs == null)
            {
                genericArgs = t.GetGenericArguments();
            }

            var genericCopier = typeof(BuiltInTypes).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(m => m.Name == copierName).FirstOrDefault();
            var concreteCopier = genericCopier.MakeGenericMethod(genericArgs);
            var copier = (SerializationManager.DeepCopier)concreteCopier.CreateDelegate(typeof(SerializationManager.DeepCopier));

            var genericSerializer = typeof(BuiltInTypes).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(m => m.Name == serializerName).FirstOrDefault();
            var concreteSerializer = genericSerializer.MakeGenericMethod(genericArgs);
            var serializer = (SerializationManager.Serializer)concreteSerializer.CreateDelegate(typeof(SerializationManager.Serializer));

            var genericDeserializer = typeof(BuiltInTypes).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(m => m.Name == deserializerName).FirstOrDefault();
            var concreteDeserializer = genericDeserializer.MakeGenericMethod(genericArgs);
            var deserializer =
                (SerializationManager.Deserializer)concreteDeserializer.CreateDelegate(typeof(SerializationManager.Deserializer));

            SerializationManager.Register(t, copier, serializer, deserializer);

            return new Tuple<SerializationManager.Serializer, SerializationManager.Deserializer, SerializationManager.DeepCopier>(serializer, deserializer, copier);
        }

        public static Tuple<SerializationManager.Serializer, SerializationManager.Deserializer, SerializationManager.DeepCopier>
            RegisterConcreteMethods(Type concreteType, Type definingType, string copierName, string serializerName, string deserializerName, Type[] genericArgs = null)
        {
            if (genericArgs == null)
            {
                genericArgs = concreteType.GetGenericArguments();
            }

            var genericCopier = definingType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(m => m.Name == copierName).FirstOrDefault();
            var concreteCopier = genericCopier.MakeGenericMethod(genericArgs);
            var copier = (SerializationManager.DeepCopier)concreteCopier.CreateDelegate(typeof(SerializationManager.DeepCopier));

            var genericSerializer = definingType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(m => m.Name == serializerName).FirstOrDefault();
            var concreteSerializer = genericSerializer.MakeGenericMethod(genericArgs);
            var serializer = (SerializationManager.Serializer)concreteSerializer.CreateDelegate(typeof(SerializationManager.Serializer));

            var genericDeserializer = definingType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(m => m.Name == deserializerName).FirstOrDefault();
            var concreteDeserializer = genericDeserializer.MakeGenericMethod(genericArgs);
            var deserializer =
                (SerializationManager.Deserializer)concreteDeserializer.CreateDelegate(typeof(SerializationManager.Deserializer));

            SerializationManager.Register(concreteType, copier, serializer, deserializer);

            return new Tuple<SerializationManager.Serializer, SerializationManager.Deserializer, SerializationManager.DeepCopier>(serializer, deserializer, copier);
        }

        #endregion
    }
}

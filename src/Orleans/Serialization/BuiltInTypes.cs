using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
        internal static void SerializeGenericReadOnlyCollection(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeReadOnlyCollection), nameof(DeserializeReadOnlyCollection), nameof(DeepCopyReadOnlyCollection), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericReadOnlyCollection(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeReadOnlyCollection), nameof(DeserializeReadOnlyCollection), nameof(DeepCopyReadOnlyCollection), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericReadOnlyCollection(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeReadOnlyCollection), nameof(DeserializeReadOnlyCollection), nameof(DeepCopyReadOnlyCollection), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeReadOnlyCollection<T>(object obj, ISerializationContext context, Type expected)
        {
            var collection = (ReadOnlyCollection<T>)obj;
            context.StreamWriter.Write(collection.Count);
            foreach (var element in collection)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeReadOnlyCollection<T>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var list = new List<T>(count);

            context.RecordObject(list);
            for (var i = 0; i < count; i++)
            {
                list.Add((T)SerializationManager.DeserializeInner(typeof(T), context));
            }

            var ret = new ReadOnlyCollection<T>(list);
            context.RecordObject(ret);
            return ret;
        }

        internal static object DeepCopyReadOnlyCollection<T>(object original, ICopyContext context)
        {
            var collection = (ReadOnlyCollection<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return original;
            }

            var innerList = new List<T>(collection.Count);
            innerList.AddRange(collection.Select(element => (T)SerializationManager.DeepCopyInner(element, context)));

            var retVal = new ReadOnlyCollection<T>(innerList);
            context.RecordCopy(original, retVal);
            return retVal;
        }

        #region Lists

        internal static void SerializeGenericList(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeList), nameof(DeserializeList), nameof(DeepCopyList), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericList(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeList), nameof(DeserializeList), nameof(DeepCopyList), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericList(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeList), nameof(DeserializeList), nameof(DeepCopyList), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeList<T>(object obj, ISerializationContext context, Type expected)
        {
            var list = (List<T>)obj;
            context.StreamWriter.Write(list.Count);
            foreach (var element in list)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeList<T>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var list = new List<T>(count);
            context.RecordObject(list);

            for (var i = 0; i < count; i++)
            {
                list.Add((T)SerializationManager.DeserializeInner(typeof(T), context));
            }
            return list;
        }

        internal static object DeepCopyList<T>(object original, ICopyContext context)
        {
            var list = (List<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new List<T>(list);
            }

            // set the list capacity, to avoid list resizing.
            var retVal = new List<T>(list.Count);
            context.RecordCopy(original, retVal);
            retVal.AddRange(list.Select(element => (T)SerializationManager.DeepCopyInner(element, context)));
            return retVal;
        }

        #endregion

        #region LinkedLists

        internal static void SerializeGenericLinkedList(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeLinkedList), nameof(DeserializeLinkedList), nameof(DeepCopyLinkedList), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericLinkedList(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeLinkedList), nameof(DeserializeLinkedList), nameof(DeepCopyLinkedList), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericLinkedList(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeLinkedList), nameof(DeserializeLinkedList), nameof(DeepCopyLinkedList), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeLinkedList<T>(object obj, ISerializationContext context, Type expected)
        {
            var list = (LinkedList<T>)obj;
            context.StreamWriter.Write(list.Count);
            foreach (var element in list)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeLinkedList<T>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var list = new LinkedList<T>();
            context.RecordObject(list);
            for (var i = 0; i < count; i++)
            {
                list.AddLast((T)SerializationManager.DeserializeInner(typeof(T), context));
            }
            return list;
        }

        internal static object DeepCopyLinkedList<T>(object original, ICopyContext context)
        {
            var list = (LinkedList<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new LinkedList<T>(list);
            }

            var retVal = new LinkedList<T>();
            context.RecordCopy(original, retVal);
            foreach (var item in list)
            {
                retVal.AddLast((T)SerializationManager.DeepCopyInner(item, context));
            }
            return retVal;
        }

        #endregion

        #region HashSets

        internal static void SerializeGenericHashSet(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeHashSet), nameof(DeserializeHashSet), nameof(DeepCopyHashSet), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericHashSet(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeHashSet), nameof(DeserializeHashSet), nameof(DeepCopyHashSet), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericHashSet(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeHashSet), nameof(DeserializeHashSet), nameof(DeepCopyHashSet), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeHashSet<T>(object obj, ISerializationContext context, Type expected)
        {
            var set = (HashSet<T>)obj;
            SerializationManager.SerializeInner(set.Comparer.Equals(EqualityComparer<T>.Default) ? null : set.Comparer,
                context, typeof(IEqualityComparer<T>));
            context.StreamWriter.Write(set.Count);
            foreach (var element in set)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeHashSet<T>(Type expected, IDeserializationContext context)
        {
            var comparer =
                (IEqualityComparer<T>)SerializationManager.DeserializeInner(typeof(IEqualityComparer<T>), context);
            var count = context.StreamReader.ReadInt();
            var set = new HashSet<T>(comparer);
            context.RecordObject(set);
            for (var i = 0; i < count; i++)
            {
                set.Add((T)SerializationManager.DeserializeInner(typeof(T), context));
            }
            return set;
        }

        internal static object DeepCopyHashSet<T>(object original, ICopyContext context)
        {
            var set = (HashSet<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new HashSet<T>(set, set.Comparer);
            }

            var retVal = new HashSet<T>(set.Comparer);
            context.RecordCopy(original, retVal);
            foreach (var item in set)
            {
                retVal.Add((T)SerializationManager.DeepCopyInner(item, context));
            }
            return retVal;
        }

        internal static void SerializeGenericSortedSet(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeSortedSet), nameof(DeserializeSortedSet), nameof(DeepCopySortedSet), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericSortedSet(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeSortedSet), nameof(DeserializeSortedSet), nameof(DeepCopySortedSet), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericSortedSet(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeSortedSet), nameof(DeserializeSortedSet), nameof(DeepCopySortedSet), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeSortedSet<T>(object obj, ISerializationContext context, Type expected)
        {
            var set = (SortedSet<T>)obj;
            SerializationManager.SerializeInner(set.Comparer.Equals(Comparer<T>.Default) ? null : set.Comparer,
                context, typeof(IComparer<T>));
            context.StreamWriter.Write(set.Count);
            foreach (var element in set)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeSortedSet<T>(Type expected, IDeserializationContext context)
        {
            var comparer =
                (IComparer<T>)SerializationManager.DeserializeInner(typeof(IComparer<T>), context);
            var count = context.StreamReader.ReadInt();
            var set = new SortedSet<T>(comparer);
            context.RecordObject(set);
            for (var i = 0; i < count; i++)
            {
                set.Add((T)SerializationManager.DeserializeInner(typeof(T), context));
            }
            return set;
        }

        internal static object DeepCopySortedSet<T>(object original, ICopyContext context)
        {
            var set = (SortedSet<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new SortedSet<T>(set, set.Comparer);
            }

            var retVal = new SortedSet<T>(set.Comparer);
            context.RecordCopy(original, retVal);
            foreach (var item in set)
            {
                retVal.Add((T)SerializationManager.DeepCopyInner(item, context));
            }
            return retVal;
        }
        #endregion

        #region Queues

        internal static void SerializeGenericQueue(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeQueue), nameof(DeserializeQueue), nameof(DeepCopyQueue), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericQueue(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeQueue), nameof(DeserializeQueue), nameof(DeepCopyQueue), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericQueue(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeQueue), nameof(DeserializeQueue), nameof(DeepCopyQueue), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeQueue<T>(object obj, ISerializationContext context, Type expected)
        {
            var queue = (Queue<T>)obj;
            context.StreamWriter.Write(queue.Count);
            foreach (var element in queue)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeQueue<T>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var queue = new Queue<T>();
            context.RecordObject(queue);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue((T)SerializationManager.DeserializeInner(typeof(T), context));
            }
            return queue;
        }

        internal static object DeepCopyQueue<T>(object original, ICopyContext context)
        {
            var queue = (Queue<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new Queue<T>(queue);
            }

            var retVal = new Queue<T>(queue.Count);
            context.RecordCopy(original, retVal);
            foreach (var item in queue)
            {
                retVal.Enqueue((T)SerializationManager.DeepCopyInner(item, context));
            }
            return retVal;
        }

        #endregion

        #region Stacks

        internal static void SerializeGenericStack(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeStack), nameof(DeserializeStack), nameof(DeepCopyStack), generics);

            concretes.Item1(original, context, expected);
        }

        internal static object DeserializeGenericStack(Type expected, IDeserializationContext context)
        {
            var generics = expected.GetGenericArguments();
            var concretes = RegisterConcreteMethods(expected, nameof(SerializeStack), nameof(DeserializeStack), nameof(DeepCopyStack), generics);

            return concretes.Item2(expected, context);
        }

        internal static object CopyGenericStack(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeStack), nameof(DeserializeStack), nameof(DeepCopyStack), generics);

            return concretes.Item3(original, context);
        }

        internal static void SerializeStack<T>(object obj, ISerializationContext context, Type expected)
        {
            var stack = (Stack<T>)obj;
            context.StreamWriter.Write(stack.Count);
            foreach (var element in stack)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeStack<T>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var list = new List<T>(count);
            var stack = new Stack<T>(count);
            context.RecordObject(stack);
            for (var i = 0; i < count; i++)
            {
                list.Add((T)SerializationManager.DeserializeInner(typeof(T), context));
            }

            for (var i = count - 1; i >= 0; i--)
            {
                stack.Push(list[i]);
            }

            return stack;
        }

        internal static object DeepCopyStack<T>(object original, ICopyContext context)
        {
            var stack = (Stack<T>)original;

            if (typeof(T).IsOrleansShallowCopyable())
            {
                return new Stack<T>(stack.Reverse()); // NOTE: Yes, the Reverse really is required
            }

            var retVal = new Stack<T>();
            context.RecordCopy(original, retVal);
            foreach (var item in stack.Reverse())
            {
                retVal.Push((T)SerializationManager.DeepCopyInner(item, context));
            }
            return retVal;
        }

        #endregion

        #region Dictionaries

        internal static void SerializeGenericDictionary(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();

            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeDictionary), nameof(DeserializeDictionary), nameof(CopyDictionary));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericDictionary(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeDictionary), nameof(DeserializeDictionary), nameof(CopyDictionary));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericDictionary(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeDictionary), nameof(DeserializeDictionary), nameof(CopyDictionary));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeDictionary<K, V>(object original, ISerializationContext context, Type expected)
        {
            var dict = (Dictionary<K, V>)original;
            SerializationManager.SerializeInner(dict.Comparer.Equals(EqualityComparer<K>.Default) ? null : dict.Comparer,
                                           context, typeof(IEqualityComparer<K>));
            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, context, typeof(K));
                SerializationManager.SerializeInner(pair.Value, context, typeof(V));
            }
        }

        internal static object DeserializeDictionary<K, V>(Type expected, IDeserializationContext context)
        {
            var comparer = (IEqualityComparer<K>)SerializationManager.DeserializeInner(typeof(IEqualityComparer<K>), context);
            var count = context.StreamReader.ReadInt();
            var dict = new Dictionary<K, V>(count, comparer);
            context.RecordObject(dict);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), context);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), context);
                dict[key] = value;
            }
            return dict;
        }

        internal static object CopyDictionary<K, V>(object original, ICopyContext context)
        {
            var dict = (Dictionary<K, V>)original;
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return new Dictionary<K, V>(dict, dict.Comparer);
            }

            var result = new Dictionary<K, V>(dict.Count, dict.Comparer);
            context.RecordCopy(original, result);
            foreach (var pair in dict)
            {
                result[(K)SerializationManager.DeepCopyInner(pair.Key, context)] = (V)SerializationManager.DeepCopyInner(pair.Value, context);
            }

            return result;
        }

        internal static void SerializeGenericReadOnlyDictionary(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeReadOnlyDictionary), nameof(DeserializeReadOnlyDictionary), nameof(CopyReadOnlyDictionary));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericReadOnlyDictionary(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeReadOnlyDictionary), nameof(DeserializeReadOnlyDictionary), nameof(CopyReadOnlyDictionary));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericReadOnlyDictionary(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeReadOnlyDictionary), nameof(DeserializeReadOnlyDictionary), nameof(CopyReadOnlyDictionary));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeReadOnlyDictionary<K, V>(object original, ISerializationContext context, Type expected)
        {
            var dict = (ReadOnlyDictionary<K, V>)original;
            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, context, typeof(K));
                SerializationManager.SerializeInner(pair.Value, context, typeof(V));
            }
        }

        internal static object DeserializeReadOnlyDictionary<K, V>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var dict = new Dictionary<K, V>(count);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), context);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), context);
                dict[key] = value;
            }

            var retVal = new ReadOnlyDictionary<K, V>(dict);
            context.RecordObject(retVal);
            return retVal;
        }

        internal static object CopyReadOnlyDictionary<K, V>(object original, ICopyContext context)
        {
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return original;
            }

            var dict = (ReadOnlyDictionary<K, V>)original;
            var innerDict = new Dictionary<K, V>(dict.Count);
            foreach (var pair in dict)
            {
                innerDict[(K)SerializationManager.DeepCopyInner(pair.Key, context)] = (V)SerializationManager.DeepCopyInner(pair.Value, context);
            }

            var retVal = new ReadOnlyDictionary<K, V>(innerDict);
            context.RecordCopy(original, retVal);
            return retVal;
        }

        internal static void SerializeStringObjectDictionary(object original, ISerializationContext context, Type expected)
        {
            var dict = (Dictionary<string, object>)original;
            SerializationManager.SerializeInner(dict.Comparer.Equals(EqualityComparer<string>.Default) ? null : dict.Comparer,
                                           context, typeof(IEqualityComparer<string>));
            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                //context.Stream.WriteTypeHeader(stringType, stringType);
                context.StreamWriter.Write(pair.Key);
                SerializationManager.SerializeInner(pair.Value, context, objectType);
            }
        }

        internal static object DeserializeStringObjectDictionary(Type expected, IDeserializationContext context)
        {
            var comparer = (IEqualityComparer<string>)SerializationManager.DeserializeInner(typeof(IEqualityComparer<string>), context);
            var count = context.StreamReader.ReadInt();
            var dict = new Dictionary<string, object>(count, comparer);
            context.RecordObject(dict);
            for (var i = 0; i < count; i++)
            {
                //context.Stream.ReadFullTypeHeader(stringType); // Skip the type header, which will be string
                var key = context.StreamReader.ReadString();
                var value = SerializationManager.DeserializeInner(null, context);
                dict[key] = value;
            }
            return dict;
        }

        internal static object CopyStringObjectDictionary(object original, ICopyContext context)
        {
            var dict = (Dictionary<string, object>)original;
            var result = new Dictionary<string, object>(dict.Count, dict.Comparer);
            context.RecordCopy(original, result);
            foreach (var pair in dict)
            {
                result[pair.Key] = SerializationManager.DeepCopyInner(pair.Value, context);
            }

            return result;
        }

        #endregion

        #region SortedDictionaries

        internal static void SerializeGenericSortedDictionary(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedDictionary), nameof(DeserializeSortedDictionary), nameof(CopySortedDictionary));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericSortedDictionary(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeSortedDictionary), nameof(DeserializeSortedDictionary), nameof(CopySortedDictionary));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericSortedDictionary(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedDictionary), nameof(DeserializeSortedDictionary), nameof(CopySortedDictionary));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeSortedDictionary<K, V>(object original, ISerializationContext context, Type expected)
        {
            var dict = (SortedDictionary<K, V>)original;
            SerializationManager.SerializeInner(dict.Comparer.Equals(Comparer<K>.Default) ? null : dict.Comparer, context, typeof(IComparer<K>));
            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, context, typeof(K));
                SerializationManager.SerializeInner(pair.Value, context, typeof(V));
            }
        }

        internal static object DeserializeSortedDictionary<K, V>(Type expected, IDeserializationContext context)
        {
            var comparer = (IComparer<K>)SerializationManager.DeserializeInner(typeof(IComparer<K>), context);
            var count = context.StreamReader.ReadInt();
            var dict = new SortedDictionary<K, V>(comparer);
            context.RecordObject(dict);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), context);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), context);
                dict[key] = value;
            }
            return dict;
        }

        internal static object CopySortedDictionary<K, V>(object original, ICopyContext context)
        {
            var dict = (SortedDictionary<K, V>)original;
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return new SortedDictionary<K, V>(dict, dict.Comparer);
            }

            var result = new SortedDictionary<K, V>(dict.Comparer);
            context.RecordCopy(original, result);
            foreach (var pair in dict)
            {
                result[(K)SerializationManager.DeepCopyInner(pair.Key, context)] = (V)SerializationManager.DeepCopyInner(pair.Value, context);
            }

            return result;
        }

        #endregion

        #region SortedLists

        internal static void SerializeGenericSortedList(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedList), nameof(DeserializeSortedList), nameof(CopySortedList));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericSortedList(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeSortedList), nameof(DeserializeSortedList), nameof(CopySortedList));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericSortedList(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeSortedList), nameof(DeserializeSortedList), nameof(CopySortedList));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeSortedList<K, V>(object original, ISerializationContext context, Type expected)
        {
            var list = (SortedList<K, V>)original;
            SerializationManager.SerializeInner(list.Comparer.Equals(Comparer<K>.Default) ? null : list.Comparer, context, typeof(IComparer<K>));
            context.StreamWriter.Write(list.Count);
            foreach (var pair in list)
            {
                SerializationManager.SerializeInner(pair.Key, context, typeof(K));
                SerializationManager.SerializeInner(pair.Value, context, typeof(V));
            }
        }

        internal static object DeserializeSortedList<K, V>(Type expected, IDeserializationContext context)
        {
            var comparer = (IComparer<K>)SerializationManager.DeserializeInner(typeof(IComparer<K>), context);
            var count = context.StreamReader.ReadInt();
            var list = new SortedList<K, V>(count, comparer);
            context.RecordObject(list);
            for (var i = 0; i < count; i++)
            {
                var key = (K)SerializationManager.DeserializeInner(typeof(K), context);
                var value = (V)SerializationManager.DeserializeInner(typeof(V), context);
                list[key] = value;
            }
            return list;
        }

        internal static object CopySortedList<K, V>(object original, ICopyContext context)
        {
            var list = (SortedList<K, V>)original;
            if (typeof(K).IsOrleansShallowCopyable() && typeof(V).IsOrleansShallowCopyable())
            {
                return new SortedList<K, V>(list, list.Comparer);
            }

            var result = new SortedList<K, V>(list.Count, list.Comparer);
            context.RecordCopy(original, result);
            foreach (var pair in list)
            {
                result[(K)SerializationManager.DeepCopyInner(pair.Key, context)] = (V)SerializationManager.DeepCopyInner(pair.Value, context);
            }

            return result;
        }

        #endregion

        #endregion

        #region Immutable Collections

        internal static void SerializeGenericImmutableDictionary(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableDictionary), nameof(DeserializeImmutableDictionary), nameof(CopyImmutableDictionary));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericImmutableDictionary(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableDictionary), nameof(DeserializeImmutableDictionary), nameof(CopyImmutableDictionary));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericImmutableDictionary(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableDictionary), nameof(DeserializeImmutableDictionary), nameof(CopyImmutableDictionary));
            return concreteMethods.Item3(original, context);
        }

        internal static object CopyImmutableDictionary<K, V>(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeImmutableDictionary<K, V>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var dict = (ImmutableDictionary<K, V>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(EqualityComparer<K>.Default) ? null : dict.KeyComparer, context, typeof(IEqualityComparer<K>));
            SerializationManager.SerializeInner(dict.ValueComparer.Equals(EqualityComparer<V>.Default) ? null : dict.ValueComparer, context, typeof(IEqualityComparer<V>));

            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, context, typeof(K));
                SerializationManager.SerializeInner(pair.Value, context, typeof(V));
            }
        }

        internal static object DeserializeImmutableDictionary<K, V>(Type expected, IDeserializationContext context)
        {
            var keyComparer = SerializationManager.DeserializeInner<IEqualityComparer<K>>(context);
            var valueComparer = SerializationManager.DeserializeInner<IEqualityComparer<V>>(context);
            var count = context.StreamReader.ReadInt();
            var dictBuilder = ImmutableDictionary.CreateBuilder(keyComparer, valueComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(context);
                var value = SerializationManager.DeserializeInner<V>(context);
                dictBuilder.Add(key, value);
            }
            var dict = dictBuilder.ToImmutable();
            context.RecordObject(dict);

            return dict;
        }

        internal static void SerializeGenericImmutableList(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableList), nameof(DeserializeImmutableList), nameof(CopyImmutableList));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericImmutableList(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableList), nameof(DeserializeImmutableList), nameof(CopyImmutableList));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericImmutableList(object original, ICopyContext context)
        {
            var t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableList), nameof(DeserializeImmutableList), nameof(CopyImmutableList));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeImmutableList<T>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var list = (ImmutableList<T>)untypedInput;
            context.StreamWriter.Write(list.Count);
            foreach (var element in list)
            {
                SerializationManager.SerializeInner(element, context, typeof(T));
            }
        }

        internal static object DeserializeImmutableList<T>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var listBuilder = ImmutableList.CreateBuilder<T>();

            for (var i = 0; i < count; i++)
            {
                listBuilder.Add(SerializationManager.DeserializeInner<T>(context));
            }
            var list = listBuilder.ToImmutable();
            context.RecordObject(list);
            return list;
        }

        internal static object CopyImmutableList<K>(object original, ICopyContext context)
        {
            return original;
        }

        internal static object CopyGenericImmutableHashSet(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableHashSet), nameof(DeserializeImmutableHashSet), nameof(CopyImmutableHashSet));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeGenericImmutableHashSet(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableHashSet), nameof(DeserializeImmutableHashSet), nameof(CopyImmutableHashSet));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericImmutableHashSet(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableHashSet), nameof(DeserializeImmutableHashSet), nameof(CopyImmutableHashSet));
            return concreteMethods.Item2(expected, context);
        }
        
        internal static object CopyImmutableHashSet<K>(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeImmutableHashSet<K>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var dict = (ImmutableHashSet<K>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(EqualityComparer<K>.Default) ? null : dict.KeyComparer, context, typeof(IEqualityComparer<K>));

            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair, context, typeof(K));
            }
        }

        internal static object DeserializeImmutableHashSet<K>(Type expected, IDeserializationContext context)
        {
            var keyComparer = SerializationManager.DeserializeInner<IEqualityComparer<K>>(context);
            var count = context.StreamReader.ReadInt();
            var dictBuilder = ImmutableHashSet.CreateBuilder(keyComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(context);
                dictBuilder.Add(key);
            }
            var dict = dictBuilder.ToImmutable();
            context.RecordObject(dict);

            return dict;
        }

        internal static object CopyGenericImmutableSortedSet(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedSet), nameof(DeserializeImmutableSortedSet), nameof(CopyImmutableSortedSet));
            return concreteMethods.Item3(original, context);
        }

        internal static object DeserializeGenericImmutableSortedSet(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableSortedSet), nameof(DeserializeImmutableSortedSet), nameof(CopyImmutableSortedSet));
            return concreteMethods.Item2(expected, context);
        }

        internal static void SerializeGenericImmutableSortedSet(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedSet), nameof(DeserializeImmutableSortedSet), nameof(CopyImmutableSortedSet));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object CopyImmutableSortedSet<K>(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeImmutableSortedSet<K>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var dict = (ImmutableSortedSet<K>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(Comparer<K>.Default) ? null : dict.KeyComparer, context, typeof(IComparer<K>));

            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair, context, typeof(K));
            }
        }

        internal static object DeserializeImmutableSortedSet<K>(Type expected, IDeserializationContext context)
        {
            var keyComparer = SerializationManager.DeserializeInner<IComparer<K>>(context);
            var count = context.StreamReader.ReadInt();
            var dictBuilder = ImmutableSortedSet.CreateBuilder(keyComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(context);
                dictBuilder.Add(key);
            }
            var dict = dictBuilder.ToImmutable();
            context.RecordObject(dict);

            return dict;
        }

        internal static object CopyGenericImmutableSortedDictionary(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedDictionary), nameof(DeserializeImmutableSortedDictionary), nameof(CopyImmutableSortedDictionary));
            return concreteMethods.Item3(original, context);
        }

        internal static object DeserializeGenericImmutableSortedDictionary(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableSortedDictionary), nameof(DeserializeImmutableSortedDictionary), nameof(CopyImmutableSortedDictionary));
            return concreteMethods.Item2(expected, context);
        }

        internal static void SerializeGenericImmutableSortedDictionary(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableSortedDictionary), nameof(DeserializeImmutableSortedDictionary), nameof(CopyImmutableSortedDictionary));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object CopyImmutableSortedDictionary<K, V>(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeImmutableSortedDictionary<K, V>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var dict = (ImmutableSortedDictionary<K, V>)untypedInput;
            SerializationManager.SerializeInner(dict.KeyComparer.Equals(Comparer<K>.Default) ? null : dict.KeyComparer, context, typeof(IComparer<K>));
            SerializationManager.SerializeInner(dict.ValueComparer.Equals(EqualityComparer<V>.Default) ? null : dict.ValueComparer, context, typeof(IEqualityComparer<V>));

            context.StreamWriter.Write(dict.Count);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair.Key, context, typeof(K));
                SerializationManager.SerializeInner(pair.Value, context, typeof(V));
            }
        }

        internal static object DeserializeImmutableSortedDictionary<K, V>(Type expected, IDeserializationContext context)
        {
            var keyComparer = SerializationManager.DeserializeInner<IComparer<K>>(context);
            var valueComparer = SerializationManager.DeserializeInner<IEqualityComparer<V>>(context);
            var count = context.StreamReader.ReadInt();
            var dictBuilder = ImmutableSortedDictionary.CreateBuilder(keyComparer, valueComparer);
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(context);
                var value = SerializationManager.DeserializeInner<V>(context);
                dictBuilder.Add(key, value);
            }
            var dict = dictBuilder.ToImmutable();
            context.RecordObject(dict);

            return dict;
        }

        internal static object CopyGenericImmutableArray(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableArray), nameof(DeserializeImmutableArray), nameof(CopyImmutableArray));
            return concreteMethods.Item3(original, context);
        }

        internal static object DeserializeGenericImmutableArray(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableArray), nameof(DeserializeImmutableArray), nameof(CopyImmutableArray));
            return concreteMethods.Item2(expected, context);
        }

        internal static void SerializeGenericImmutableArray(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableArray), nameof(DeserializeImmutableArray), nameof(CopyImmutableArray));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object CopyImmutableArray<K>(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeImmutableArray<K>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var dict = (ImmutableArray<K>)untypedInput;

            context.StreamWriter.Write(dict.Length);
            foreach (var pair in dict)
            {
                SerializationManager.SerializeInner(pair, context, typeof(K));
            }
        }

        internal static object DeserializeImmutableArray<K>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var dictBuilder = ImmutableArray.CreateBuilder<K>();
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(context);
                dictBuilder.Add(key);
            }
            var dict = dictBuilder.ToImmutable();
            context.RecordObject(dict);

            return dict;
        }
        internal static object CopyGenericImmutableQueue(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableQueue), nameof(DeserializeImmutableQueue), nameof(CopyImmutableQueue));
            return concreteMethods.Item3(original, context);
        }

        internal static object DeserializeGenericImmutableQueue(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutableQueue), nameof(DeserializeImmutableQueue), nameof(CopyImmutableQueue));
            return concreteMethods.Item2(expected, context);
        }

        internal static void SerializeGenericImmutableQueue(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutableQueue), nameof(DeserializeImmutableQueue), nameof(CopyImmutableQueue));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object CopyImmutableQueue<K>(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeImmutableQueue<K>(object untypedInput, ISerializationContext context, Type typeExpected)
        {
            var queue = (ImmutableQueue<K>)untypedInput;

            context.StreamWriter.Write(queue.Count());
            foreach (var item in queue)
            {
                SerializationManager.SerializeInner(item, context, typeof(K));
            }
        }

        internal static object DeserializeImmutableQueue<K>(Type expected, IDeserializationContext context)
        {
            var count = context.StreamReader.ReadInt();
            var items = new K[count];
            for (var i = 0; i < count; i++)
            {
                var key = SerializationManager.DeserializeInner<K>(context);
                items[i] = key;
            }
            var queues = ImmutableQueue.CreateRange(items);

            context.RecordObject(queues);

            return queues;
        }
        #endregion

        #region Other generics

        #region Tuples

        internal static void SerializeTuple(object raw, ISerializationContext context, Type expected)
        {
            Type t = raw.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeTuple) + generics.Length, nameof(DeserializeTuple) + generics.Length, nameof(DeepCopyTuple) + generics.Length, generics);

            concretes.Item1(raw, context, expected);
        }

        internal static object DeserializeTuple(Type t, IDeserializationContext context)
        {
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeTuple) + generics.Length, nameof(DeserializeTuple) + generics.Length, nameof(DeepCopyTuple) + generics.Length, generics);

            return concretes.Item2(t, context);
        }

        internal static object DeepCopyTuple(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var generics = t.GetGenericArguments();
            var concretes = RegisterConcreteMethods(t, nameof(SerializeTuple) + generics.Length, nameof(DeserializeTuple) + generics.Length, nameof(DeepCopyTuple) + generics.Length, generics);

            return concretes.Item3(original, context);
        }

        internal static object DeepCopyTuple1<T1>(object original, ICopyContext context)
        {
            var input = (Tuple<T1>)original;
            var result = new Tuple<T1>((T1)SerializationManager.DeepCopyInner(input.Item1, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple1<T1>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
        }

        internal static object DeserializeTuple1<T1>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            return new Tuple<T1>(item1);
        }

        internal static object DeepCopyTuple2<T1, T2>(object original, ICopyContext context)
        {
            var input = (Tuple<T1, T2>)original;
            var result = new Tuple<T1, T2>((T1)SerializationManager.DeepCopyInner(input.Item1, context), (T2)SerializationManager.DeepCopyInner(input.Item2, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple2<T1, T2>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1, T2>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, context, typeof(T2));
        }

        internal static object DeserializeTuple2<T1, T2>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), context);
            return new Tuple<T1, T2>(item1, item2);
        }

        internal static object DeepCopyTuple3<T1, T2, T3>(object original, ICopyContext context)
        {
            var input = (Tuple<T1, T2, T3>)original;
            var result = new Tuple<T1, T2, T3>((T1)SerializationManager.DeepCopyInner(input.Item1, context), (T2)SerializationManager.DeepCopyInner(input.Item2, context),
                (T3)SerializationManager.DeepCopyInner(input.Item3, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple3<T1, T2, T3>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1, T2, T3>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, context, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, context, typeof(T3));
        }

        internal static object DeserializeTuple3<T1, T2, T3>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), context);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), context);
            return new Tuple<T1, T2, T3>(item1, item2, item3);
        }

        internal static object DeepCopyTuple4<T1, T2, T3, T4>(object original, ICopyContext context)
        {
            var input = (Tuple<T1, T2, T3, T4>)original;
            var result = new Tuple<T1, T2, T3, T4>((T1)SerializationManager.DeepCopyInner(input.Item1, context), (T2)SerializationManager.DeepCopyInner(input.Item2, context),
                (T3)SerializationManager.DeepCopyInner(input.Item3, context),
                (T4)SerializationManager.DeepCopyInner(input.Item4, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple4<T1, T2, T3, T4>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, context, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, context, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, context, typeof(T4));
        }

        internal static object DeserializeTuple4<T1, T2, T3, T4>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), context);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), context);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), context);
            return new Tuple<T1, T2, T3, T4>(item1, item2, item3, item4);
        }

        internal static object DeepCopyTuple5<T1, T2, T3, T4, T5>(object original, ICopyContext context)
        {
            var input = (Tuple<T1, T2, T3, T4, T5>)original;
            var result = new Tuple<T1, T2, T3, T4, T5>((T1)SerializationManager.DeepCopyInner(input.Item1, context), (T2)SerializationManager.DeepCopyInner(input.Item2, context),
                (T3)SerializationManager.DeepCopyInner(input.Item3, context),
                (T4)SerializationManager.DeepCopyInner(input.Item4, context),
                (T5)SerializationManager.DeepCopyInner(input.Item5, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple5<T1, T2, T3, T4, T5>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4, T5>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, context, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, context, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, context, typeof(T4));
            SerializationManager.SerializeInner(input.Item5, context, typeof(T5));
        }

        internal static object DeserializeTuple5<T1, T2, T3, T4, T5>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), context);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), context);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), context);
            var item5 = (T5)SerializationManager.DeserializeInner(typeof(T5), context);
            return new Tuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
        }

        internal static object DeepCopyTuple6<T1, T2, T3, T4, T5, T6>(object original, ICopyContext context)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6>)original;
            var result = new Tuple<T1, T2, T3, T4, T5, T6>((T1)SerializationManager.DeepCopyInner(input.Item1, context), (T2)SerializationManager.DeepCopyInner(input.Item2, context),
                (T3)SerializationManager.DeepCopyInner(input.Item3, context),
                (T4)SerializationManager.DeepCopyInner(input.Item4, context),
                (T5)SerializationManager.DeepCopyInner(input.Item5, context),
                (T6)SerializationManager.DeepCopyInner(input.Item6, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple6<T1, T2, T3, T4, T5, T6>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, context, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, context, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, context, typeof(T4));
            SerializationManager.SerializeInner(input.Item5, context, typeof(T5));
            SerializationManager.SerializeInner(input.Item6, context, typeof(T6));
        }

        internal static object DeserializeTuple6<T1, T2, T3, T4, T5, T6>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), context);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), context);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), context);
            var item5 = (T5)SerializationManager.DeserializeInner(typeof(T5), context);
            var item6 = (T6)SerializationManager.DeserializeInner(typeof(T6), context);
            return new Tuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
        }

        internal static object DeepCopyTuple7<T1, T2, T3, T4, T5, T6, T7>(object original, ICopyContext context)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6, T7>)original;
            var result = new Tuple<T1, T2, T3, T4, T5, T6, T7>((T1)SerializationManager.DeepCopyInner(input.Item1, context), (T2)SerializationManager.DeepCopyInner(input.Item2, context),
                (T3)SerializationManager.DeepCopyInner(input.Item3, context),
                (T4)SerializationManager.DeepCopyInner(input.Item4, context),
                (T5)SerializationManager.DeepCopyInner(input.Item5, context),
                (T6)SerializationManager.DeepCopyInner(input.Item6, context),
                (T7)SerializationManager.DeepCopyInner(input.Item7, context));
            context.RecordCopy(original, result);
            return result;
        }

        internal static void SerializeTuple7<T1, T2, T3, T4, T5, T6, T7>(object obj, ISerializationContext context, Type expected)
        {
            var input = (Tuple<T1, T2, T3, T4, T5, T6, T7>)obj;
            SerializationManager.SerializeInner(input.Item1, context, typeof(T1));
            SerializationManager.SerializeInner(input.Item2, context, typeof(T2));
            SerializationManager.SerializeInner(input.Item3, context, typeof(T3));
            SerializationManager.SerializeInner(input.Item4, context, typeof(T4));
            SerializationManager.SerializeInner(input.Item5, context, typeof(T5));
            SerializationManager.SerializeInner(input.Item6, context, typeof(T6));
            SerializationManager.SerializeInner(input.Item7, context, typeof(T7));
        }

        internal static object DeserializeTuple7<T1, T2, T3, T4, T5, T6, T7>(Type expected, IDeserializationContext context)
        {
            var item1 = (T1)SerializationManager.DeserializeInner(typeof(T1), context);
            var item2 = (T2)SerializationManager.DeserializeInner(typeof(T2), context);
            var item3 = (T3)SerializationManager.DeserializeInner(typeof(T3), context);
            var item4 = (T4)SerializationManager.DeserializeInner(typeof(T4), context);
            var item5 = (T5)SerializationManager.DeserializeInner(typeof(T5), context);
            var item6 = (T6)SerializationManager.DeserializeInner(typeof(T6), context);
            var item7 = (T7)SerializationManager.DeserializeInner(typeof(T7), context);
            return new Tuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
        }

        #endregion

        #region KeyValuePairs

        internal static void SerializeGenericKeyValuePair(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeKeyValuePair), nameof(DeserializeKeyValuePair), nameof(CopyKeyValuePair));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericKeyValuePair(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeKeyValuePair), nameof(DeserializeKeyValuePair), nameof(CopyKeyValuePair));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericKeyValuePair(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeKeyValuePair), nameof(DeserializeKeyValuePair), nameof(CopyKeyValuePair));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeKeyValuePair<TK, TV>(object original, ISerializationContext context, Type expected)
        {
            var pair = (KeyValuePair<TK, TV>)original;
            SerializationManager.SerializeInner(pair.Key, context, typeof(TK));
            SerializationManager.SerializeInner(pair.Value, context, typeof(TV));
        }

        internal static object DeserializeKeyValuePair<K, V>(Type expected, IDeserializationContext context)
        {
            var key = (K)SerializationManager.DeserializeInner(typeof(K), context);
            var value = (V)SerializationManager.DeserializeInner(typeof(V), context);
            return new KeyValuePair<K, V>(key, value);
        }

        internal static object CopyKeyValuePair<TK, TV>(object original, ICopyContext context)
        {
            var pair = (KeyValuePair<TK, TV>)original;
            if (typeof(TK).IsOrleansShallowCopyable() && typeof(TV).IsOrleansShallowCopyable())
            {
                return pair;    // KeyValuePair is a struct, so there's already been a copy at this point
            }

            var result = new KeyValuePair<TK, TV>((TK)SerializationManager.DeepCopyInner(pair.Key, context), (TV)SerializationManager.DeepCopyInner(pair.Value, context));
            context.RecordCopy(original, result);
            return result;
        }

        #endregion

        #region Nullables

        internal static void SerializeGenericNullable(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeNullable), nameof(DeserializeNullable), nameof(CopyNullable));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericNullable(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeNullable), nameof(DeserializeNullable), nameof(CopyNullable));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericNullable(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeNullable), nameof(DeserializeNullable), nameof(CopyNullable));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeNullable<T>(object original, ISerializationContext context, Type expected) where T : struct
        {
            var obj = (T?)original;
            if (obj.HasValue)
            {
                SerializationManager.SerializeInner(obj.Value, context, typeof(T));
            }
            else
            {
                context.StreamWriter.WriteNull();
            }
        }

        internal static object DeserializeNullable<T>(Type expected, IDeserializationContext context) where T : struct
        {
            if (context.StreamReader.PeekToken() == SerializationTokenType.Null)
            {
                context.StreamReader.ReadToken();
                return new T?();
            }

            var val = (T)SerializationManager.DeserializeInner(typeof(T), context);
            return new Nullable<T>(val);
        }

        internal static object CopyNullable<T>(object original, ICopyContext context) where T : struct
        {
            return original;    // Everything is a struct, so a direct copy is fine
        }

        #endregion

        #region Immutables

        internal static void SerializeGenericImmutable(object original, ISerializationContext context, Type expected)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutable), nameof(DeserializeImmutable), nameof(CopyImmutable));
            concreteMethods.Item1(original, context, expected);
        }

        internal static object DeserializeGenericImmutable(Type expected, IDeserializationContext context)
        {
            var concreteMethods = RegisterConcreteMethods(expected, nameof(SerializeImmutable), nameof(DeserializeImmutable), nameof(CopyImmutable));
            return concreteMethods.Item2(expected, context);
        }

        internal static object CopyGenericImmutable(object original, ICopyContext context)
        {
            Type t = original.GetType();
            var concreteMethods = RegisterConcreteMethods(t, nameof(SerializeImmutable), nameof(DeserializeImmutable), nameof(CopyImmutable));
            return concreteMethods.Item3(original, context);
        }

        internal static void SerializeImmutable<T>(object original, ISerializationContext context, Type expected)
        {
            var obj = (Immutable<T>)original;
            SerializationManager.SerializeInner(obj.Value, context, typeof(T));
        }

        internal static object DeserializeImmutable<T>(Type expected, IDeserializationContext context)
        {
            var val = (T)SerializationManager.DeserializeInner(typeof(T), context);
            return new Immutable<T>(val);
        }

        internal static object CopyImmutable<T>(object original, ICopyContext context)
        {
            return original;    // Immutable means never having to make a copy...
        }

        #endregion

        #endregion

        #region Other System types

        #region TimeSpan

        internal static void SerializeTimeSpan(object obj, ISerializationContext context, Type expected)
        {
            var ts = (TimeSpan)obj;
            context.StreamWriter.Write(ts.Ticks);
        }

        internal static object DeserializeTimeSpan(Type expected, IDeserializationContext context)
        {
            return new TimeSpan(context.StreamReader.ReadLong());
        }

        internal static object CopyTimeSpan(object obj, ICopyContext context)
        {
            return obj; // TimeSpan is a value type 
        }

        #endregion

        #region DateTimeOffset

        internal static void SerializeDateTimeOffset(object obj, ISerializationContext context, Type expected)
        {
            var dts = (DateTimeOffset)obj;
            context.StreamWriter.Write(dts.DateTime.Ticks);
            context.StreamWriter.Write(dts.Offset.Ticks);
        }

        internal static object DeserializeDateTimeOffset(Type expected, IDeserializationContext context)
        {
            return new DateTimeOffset(context.StreamReader.ReadLong(), new TimeSpan(context.StreamReader.ReadLong()));
        }

        internal static object CopyDateTimeOffset(object obj, ICopyContext context)
        {
            return obj; // DateTimeOffset is a value type 
        }

        #endregion

        #region Type

        internal static void SerializeType(object obj, ISerializationContext context, Type expected)
        {
            context.StreamWriter.Write(((Type)obj).OrleansTypeKeyString());
        }

        internal static object DeserializeType(Type expected, IDeserializationContext context)
        {
            return SerializationManager.ResolveTypeName(context.StreamReader.ReadString());
        }

        internal static object CopyType(object obj, ICopyContext context)
        {
            return obj; // Type objects are effectively immutable
        }

        #endregion Type

        #region GUID

        internal static void SerializeGuid(object obj, ISerializationContext context, Type expected)
        {
            var guid = (Guid)obj;
            context.StreamWriter.Write(guid.ToByteArray());
        }

        internal static object DeserializeGuid(Type expected, IDeserializationContext context)
        {
            var bytes = context.StreamReader.ReadBytes(16);
            return new Guid(bytes);
        }

        internal static object CopyGuid(object obj, ICopyContext context)
        {
            return obj; // Guids are value types
        }

        #endregion

        #region URIs

        [ThreadStatic]
        static private TypeConverter uriConverter;

        internal static void SerializeUri(object obj, ISerializationContext context, Type expected)
        {
            if (uriConverter == null) uriConverter = TypeDescriptor.GetConverter(typeof(Uri));
            context.StreamWriter.Write(uriConverter.ConvertToInvariantString(obj));
        }

        internal static object DeserializeUri(Type expected, IDeserializationContext context)
        {
            if (uriConverter == null) uriConverter = TypeDescriptor.GetConverter(typeof(Uri));
            return uriConverter.ConvertFromInvariantString(context.StreamReader.ReadString());
        }

        internal static object CopyUri(object obj, ICopyContext context)
        {
            return obj; // URIs are immutable
        }

        #endregion

        #region CultureInfo

        internal static void SerializeCultureInfo(object obj, ISerializationContext context, Type expected)
        {
            var cultureInfo = (CultureInfo)obj;
            context.StreamWriter.Write(cultureInfo.Name);
        }

        internal static object DeserializeCultureInfo(Type expected, IDeserializationContext context)
        {           
            return new CultureInfo(context.StreamReader.ReadString());
        }

        internal static object CopyCultureInfo(object obj, ICopyContext context)
        {
            return obj;
        }

        #endregion

        #endregion

        #region Internal Orleans types

        #region Basic types

        internal static void SerializeGrainId(object obj, ISerializationContext context, Type expected)
        {
            var id = (GrainId)obj;
            context.StreamWriter.Write(id);
        }

        internal static object DeserializeGrainId(Type expected, IDeserializationContext context)
        {
            return context.StreamReader.ReadGrainId();
        }

        internal static object CopyGrainId(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeActivationId(object obj, ISerializationContext context, Type expected)
        {
            var id = (ActivationId)obj;
            context.StreamWriter.Write(id);
        }

        internal static object DeserializeActivationId(Type expected, IDeserializationContext context)
        {
            return context.StreamReader.ReadActivationId();
        }

        internal static object CopyActivationId(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeActivationAddress(object obj, ISerializationContext context, Type expected)
        {
            var addr = (ActivationAddress)obj;
            context.StreamWriter.Write(addr);
        }

        internal static object DeserializeActivationAddress(Type expected, IDeserializationContext context)
        {
            return context.StreamReader.ReadActivationAddress();
        }

        internal static object CopyActivationAddress(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeIPAddress(object obj, ISerializationContext context, Type expected)
        {
            var ip = (IPAddress)obj;
            context.StreamWriter.Write(ip);
        }

        internal static object DeserializeIPAddress(Type expected, IDeserializationContext context)
        {
            return context.StreamReader.ReadIPAddress();
        }

        internal static object CopyIPAddress(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeIPEndPoint(object obj, ISerializationContext context, Type expected)
        {
            var ep = (IPEndPoint)obj;
            context.StreamWriter.Write(ep);
        }

        internal static object DeserializeIPEndPoint(Type expected, IDeserializationContext context)
        {
            return context.StreamReader.ReadIPEndPoint();
        }

        internal static object CopyIPEndPoint(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeCorrelationId(object obj, ISerializationContext context, Type expected)
        {
            var id = (CorrelationId)obj;
            context.StreamWriter.Write(id);
        }

        internal static object DeserializeCorrelationId(Type expected, IDeserializationContext context)
        {
            var bytes = context.StreamReader.ReadBytes(CorrelationId.SIZE_BYTES);
            return new CorrelationId(bytes);
        }

        internal static object CopyCorrelationId(object original, ICopyContext context)
        {
            return original;
        }

        internal static void SerializeSiloAddress(object obj, ISerializationContext context, Type expected)
        {
            var addr = (SiloAddress)obj;
            context.StreamWriter.Write(addr);
        }

        internal static object DeserializeSiloAddress(Type expected, IDeserializationContext context)
        {
            return context.StreamReader.ReadSiloAddress();
        }

        internal static object CopySiloAddress(object original, ICopyContext context)
        {
            return original;
        }

        internal static object CopyTaskId(object original, ICopyContext context)
        {
            return original;
        }

        #endregion

        #region InvokeMethodRequest

        internal static void SerializeInvokeMethodRequest(object obj, ISerializationContext context, Type expected)
        {
            var request = (InvokeMethodRequest)obj;

            context.StreamWriter.Write(request.InterfaceId);
            context.StreamWriter.Write(request.MethodId);
            context.StreamWriter.Write(request.Arguments != null ? request.Arguments.Length : 0);
            if (request.Arguments != null)
            {
                foreach (var arg in request.Arguments)
                {
                    SerializationManager.SerializeInner(arg, context, null);
                }
            }
        }

        internal static object DeserializeInvokeMethodRequest(Type expected, IDeserializationContext context)
        {
            int iid = context.StreamReader.ReadInt();
            int mid = context.StreamReader.ReadInt();

            int argCount = context.StreamReader.ReadInt();
            object[] args = null;

            if (argCount > 0)
            {
                args = new object[argCount];
                for (var i = 0; i < argCount; i++)
                {
                    args[i] = SerializationManager.DeserializeInner(null, context);
                }
            }

            return new InvokeMethodRequest(iid, mid, args);
        }

        internal static object CopyInvokeMethodRequest(object original, ICopyContext context)
        {
            var request = (InvokeMethodRequest)original;

            object[] args = null;
            if (request.Arguments != null)
            {
                args = new object[request.Arguments.Length];
                for (var i = 0; i < request.Arguments.Length; i++)
                {
                    args[i] = SerializationManager.DeepCopyInner(request.Arguments[i], context);
                }
            }

            var result = new InvokeMethodRequest(request.InterfaceId, request.MethodId, args);
            context.RecordCopy(original, result);
            return result;
        }

        #endregion

        #region Response

        internal static void SerializeOrleansResponse(object obj, ISerializationContext context, Type expected)
        {
            var resp = (Response)obj;

            SerializationManager.SerializeInner(resp.ExceptionFlag ? resp.Exception : resp.Data, context, null);
        }

        internal static object DeserializeOrleansResponse(Type expected, IDeserializationContext context)
        {
            var obj = SerializationManager.DeserializeInner(null, context);
            return new Response(obj);
        }

        internal static object CopyOrleansResponse(object original, ICopyContext context)
        {
            var resp = (Response)original;

            if (resp.ExceptionFlag)
            {
                return original;
            }

            var result = new Response(SerializationManager.DeepCopyInner(resp.Data, context));
            context.RecordCopy(original, result);
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

            var genericCopier = typeof(BuiltInTypes).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == copierName);
            var concreteCopier = genericCopier.MakeGenericMethod(genericArgs);
            var copier = (SerializationManager.DeepCopier)concreteCopier.CreateDelegate(typeof(SerializationManager.DeepCopier));

            var genericSerializer = typeof(BuiltInTypes).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == serializerName);
            var concreteSerializer = genericSerializer.MakeGenericMethod(genericArgs);
            var serializer = (SerializationManager.Serializer)concreteSerializer.CreateDelegate(typeof(SerializationManager.Serializer));

            var genericDeserializer = typeof(BuiltInTypes).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == deserializerName);
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

            var genericCopier = definingType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == copierName);
            var concreteCopier = genericCopier.MakeGenericMethod(genericArgs);
            var copier = (SerializationManager.DeepCopier)concreteCopier.CreateDelegate(typeof(SerializationManager.DeepCopier));

            var genericSerializer = definingType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == serializerName);
            var concreteSerializer = genericSerializer.MakeGenericMethod(genericArgs);
            var serializer = (SerializationManager.Serializer)concreteSerializer.CreateDelegate(typeof(SerializationManager.Serializer));

            var genericDeserializer = definingType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(m => m.Name == deserializerName);
            var concreteDeserializer = genericDeserializer.MakeGenericMethod(genericArgs);
            var deserializer =
                (SerializationManager.Deserializer)concreteDeserializer.CreateDelegate(typeof(SerializationManager.Deserializer));

            SerializationManager.Register(concreteType, copier, serializer, deserializer);

            return new Tuple<SerializationManager.Serializer, SerializationManager.Deserializer, SerializationManager.DeepCopier>(serializer, deserializer, copier);
        }

        #endregion
    }
}

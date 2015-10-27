/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using Orleans.CodeGeneration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Bond;

#if BOND_FAST_SERIALIZER
using BondBinaryWriter = Bond.Protocols.FastBinaryWriter<Orleans.Serialization.Bond.BondOutputStream>;
using BondTypeSerializer = Bond.Serialize<Bond.Protocols.FastBinaryWriter<Orleans.Serialization.Bond.BondOutputStream>>;
using BondBinaryReader = Bond.Protocols.FastBinaryReader<Orleans.Serialization.Bond.BondInputStream>;
using BondTypeDeserializer = Bond.Deserialize<Bond.Protocols.FastBinaryReader<Orleans.Serialization.Bond.BondInputStream>>;

#elif BOND_COMPACT_SERIALIZER
using BondBinaryWriter = Bond.Protocols.CompactBinaryWriter<Orleans.Serialization.Bond.BondOutputStream>;
using BondTypeSerializer = Bond.Serialize<Bond.Protocols.CompactBinaryWriter<Orleans.Serialization.Bond.BondOutputStream>>;
using BondBinaryReader = Bond.Protocols.CompactBinaryReader<Orleans.Serialization.Bond.BondInputStream>;
using BondTypeDeserializer = Bond.Deserialize<Bond.Protocols.CompactBinaryReader<Orleans.Serialization.Bond.BondInputStream>>;
#else
using BondBinaryWriter = Bond.Protocols.SimpleBinaryWriter<Orleans.Serialization.Bond.BondOutputStream>;
using BondTypeSerializer = Bond.Serializer<Bond.Protocols.SimpleBinaryWriter<Orleans.Serialization.Bond.BondOutputStream>>;
using BondBinaryReader = Bond.Protocols.SimpleBinaryReader<Orleans.Serialization.Bond.BondInputStream>;
using BondTypeDeserializer = Bond.Deserializer<Bond.Protocols.SimpleBinaryReader<Orleans.Serialization.Bond.BondInputStream>>;
#endif

using System.IO;
using Orleans.Runtime;

namespace Orleans.Serialization.Bond
{

    [RegisterSerializer]
    public static class BondSerializer
    {
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, Delegate> CopierDictionary = new ConcurrentDictionary<RuntimeTypeHandle, Delegate>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, BondTypeSerializer> SerializerDictionary = new ConcurrentDictionary<RuntimeTypeHandle, BondTypeSerializer>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, BondTypeDeserializer> DeserializerDictionary = new ConcurrentDictionary<RuntimeTypeHandle, BondTypeDeserializer>();
        private static readonly HashSet<RuntimeTypeHandle> BondGenericTypes = new HashSet<RuntimeTypeHandle>();

        private static readonly Logger Logger = TraceLogger.GetLogger("BondSerializer", TraceLogger.LoggerType.Runtime);

        public static void Initialize(string preloadAssemblies)
        {
            Action<Assembly> discoverAction = assembly =>
                assembly.GetTypes()
                    .Where(type => type.IsClass && type.GetCustomAttribute<SchemaAttribute>() != null)
                    .ToList()
                    .ForEach(Register);

            AppDomain.CurrentDomain.GetAssemblies().ToList().ForEach(discoverAction);
            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => discoverAction(args.LoadedAssembly);
            if (string.IsNullOrWhiteSpace(preloadAssemblies))
            {
                return;
            }

            var names = preloadAssemblies.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in names)
            {
                try
                {
                    var assemblyName = new AssemblyName(name);
                    AppDomain.CurrentDomain.Load(assemblyName);
                }
                catch (FileNotFoundException e)
                {
                    LogWarning(0, e, "Failed to find assembly: {0}", name);
                }
                catch (BadImageFormatException e)
                {
                    LogWarning(0, e, "{0} has an invalid image format", name);
                }
                catch (FileLoadException e)
                {
                    LogWarning(0, e, "{0} failed to load", name);
                }
            }
        }

        public static object DeepCopy(object original)
        {
            if (original == null)
            {
                return null;
            }

            var copier = GetCopier(original.GetType().TypeHandle);
            if (copier == null)
            {
                LogWarning(1, "no copier found for type {0}", original.GetType());
                throw new ArgumentOutOfRangeException("no copier provided for the selected type", "original");
            }

            return copier.DynamicInvoke(original);
        }

        public static void Serialize(object untypedInput, BinaryTokenStreamWriter stream, Type expected)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            if (untypedInput == null)
            {
                stream.WriteNull();
                return;
            }

            var typeHandle = untypedInput.GetType().TypeHandle;
            var serializer = GetSerializer(typeHandle);
            if (serializer == null)
            {
                LogWarning(2, "no serializer found for type {0}", untypedInput.GetType());
                throw new ArgumentOutOfRangeException("no serializer provided fro the selected type", "untypedInput");
            }

            var outputStream = BondOutputStream.Create(stream);
            var writer = new BondBinaryWriter(outputStream);
            serializer.Serialize(untypedInput, writer);
        }

        public static object Deserialize(Type expected, BinaryTokenStreamReader stream)
        {
            if (expected == null)
            {
                throw new ArgumentNullException("expected");
            }

            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            var typeHandle = expected.TypeHandle;
            var deserializer = GetDeserializer(typeHandle);
            if (deserializer == null)
            {
                LogWarning(3, "no serializer found for type {0}", expected.FullName);
                throw new ArgumentOutOfRangeException("no serializer provided fro the selected type", "expected");
            }

            var inputStream = BondInputStream.Create(stream);
            var reader = new BondBinaryReader(inputStream);
            return deserializer.Deserialize(reader);
        }

        internal static bool TrySerialize(BinaryTokenStreamWriter writer, Type expectedType, object item)
        {
            var itemType = item.GetType();
            // this method will only be called for a non-bond item or a bond item with generic parameters
            if (itemType.IsGenericType == false)
            {
                // it's not a bond type
                return false;
            }

            // check to see if we're already late-bound
            BondTypeSerializer serializer;
            if (SerializerDictionary.TryGetValue(itemType.TypeHandle, out serializer))
            {
                // another thread beat us to adding the serializer
                writer.WriteTypeHeader(itemType, expectedType);
                Serialize(item, writer, expectedType);
                return true;
            }

            // make sure we're dealing with a bond type
            var genericType = itemType.GetGenericTypeDefinition();
            if (BondGenericTypes.Contains(genericType.TypeHandle) == false)
            {
                // it's not a bond generic type
                return false;
            }

            // register a new serializer
            Register(itemType);
            writer.WriteTypeHeader(itemType, expectedType);
            Serialize(item, writer, expectedType);
            return true;
        }

        internal static bool TryDeserialize(Type expectedType, BinaryTokenStreamReader reader, out object item)
        {
            // this method will only be called for a non-bond item or a bond item with generic parameters
            if (expectedType.IsGenericType == false)
            {
                // it's not a bond type
                item = null;
                return false;
            }

            // check to see if we're already late-bound
            BondTypeDeserializer deserializer;
            if (DeserializerDictionary.TryGetValue(expectedType.TypeHandle, out deserializer))
            {
                // another thread beat us to adding the deserializer
                item = Deserialize(expectedType, reader);
                return true;
            }

            // make sure we're dealing with a bond type
            var genericType = expectedType.GetGenericTypeDefinition();
            if (BondGenericTypes.Contains(genericType.TypeHandle) == false)
            {
                // it's not a bond generic type
                item = null;
                return false;
            }

            // register a new deserializer
            Register(expectedType);
            item = Deserialize(expectedType, reader);
            return true;
        }

        internal static bool TryCopy(object source, out object destination)
        {
            if (source == null)
            {
                destination = null;
                return true;
            }

            var itemType = source.GetType();

            // this method will only be called for a non-bond item or a bond item with generic parameters
            if (itemType.IsGenericType == false)
            {
                // it's not a bond type
                destination = null;
                return false;
            }

            // check to see if we're already late-bound
            Delegate copier;
            if (CopierDictionary.TryGetValue(itemType.TypeHandle, out copier))
            {
                // another thread beat us to adding the copier
                destination = copier.DynamicInvoke(source);
                return true;
            }

            // make sure we're dealing with a bond type
            var genericType = itemType.GetGenericTypeDefinition();
            if (BondGenericTypes.Contains(genericType.TypeHandle) == false)
            {
                // it's not a bond generic type
                destination = null;
                return false;
            }

            // register a new copier
            Register(itemType);
            destination = DeepCopy(source);
            return true;
        }

        private static void Register(Type type)
        {
            if (type.IsGenericType && type.IsConstructedGenericType == false)
            {
                BondGenericTypes.Add(type.TypeHandle);
                return;
            }

            var clonerType = typeof(Cloner<>);
            var realType = clonerType.MakeGenericType(type);
            var clonerInstance = Activator.CreateInstance(realType);
            var cloneMethod = realType.GetMethod("Clone").MakeGenericMethod(type);
            var copierDelegate = cloneMethod.CreateDelegate(
                    typeof(Func<,>).MakeGenericType(new[] { type, type }),
                    clonerInstance);
            var serializer = new BondTypeSerializer(type);
            var deserializer = new BondTypeDeserializer(type);
            CopierDictionary.TryAdd(type.TypeHandle, copierDelegate);
            SerializerDictionary.TryAdd(type.TypeHandle, serializer);
            DeserializerDictionary.TryAdd(type.TypeHandle, deserializer);
            SerializationManager.Register(type, DeepCopy, Serialize, Deserialize);
        }

        private static Delegate GetCopier(RuntimeTypeHandle handle)
        {
            return Get(CopierDictionary, handle);
        }

        private static BondTypeSerializer GetSerializer(RuntimeTypeHandle handle)
        {
            return Get(SerializerDictionary, handle);
        }

        private static BondTypeDeserializer GetDeserializer(RuntimeTypeHandle handle)
        {
            return Get(DeserializerDictionary, handle);
        }

        private static TValue Get<TValue>(IDictionary<RuntimeTypeHandle, TValue> dictionary, RuntimeTypeHandle key)
        {
            TValue value;
            dictionary.TryGetValue(key, out value);
            return value;
        }

        private static void LogWarning(int code, Exception e, string format, params object[] parameters)
        {
            if (Logger.IsWarning == false)
            {
                return;
            }

            Logger.Warn(code, string.Format(format, parameters), e);
        }

        private static void LogWarning(int code, string format, params object[] parameters)
        {
            if (Logger.IsWarning == false)
            {
                return;
            }

            Logger.Warn(code, format, parameters);
        }
    }
}

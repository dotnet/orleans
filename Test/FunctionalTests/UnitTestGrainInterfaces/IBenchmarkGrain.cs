using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;
using System.Collections;

namespace UnitTestGrains
{
    public interface IBenchmarkGrain : IGrain
    {
        Task<string> GetName();

        Task<IBenchmarkGrain> GetOther();

        Task<int> GetDummyDelay();

        Task Ping();

        Task DummyRead();
        Task DummyWrite();

        Task ReadOther(int count);
        Task WriteOther(int count);

        Task WriteData(object data);

        Task<byte> ReadByte(int messageSize);
        Task<float> ReadFloat(int messageSize);
        Task<string> ReadString(int messageSize);
        Task<BenchmarkGrainDataClass> ReadClass(int messageSize);
        Task<BenchmarkGrainDataStruct> ReadStruct(int messageSize);
        Task<byte[]> ReadArrayByte(int messageSize);
        Task<float[]> ReadArrayFloat(int messageSize);
        Task<string[]> ReadArrayString(int messageSize);
        Task<BenchmarkGrainDataClass[]> ReadArrayClass(int messageSize);
        Task<BenchmarkGrainDataStruct[]> ReadArrayStruct(int messageSize);
        Task<Dictionary<string, byte>> ReadDictionaryByte(int messageSize);
        Task<Dictionary<string, float>> ReadDictionaryFloat(int messageSize);
        Task<Dictionary<string, string>> ReadDictionaryString(int messageSize);
        Task<Dictionary<string, BenchmarkGrainDataClass>> ReadDictionaryClass(int messageSize);
        Task<Dictionary<string, BenchmarkGrainDataStruct>> ReadDictionaryStruct(int messageSize);
        Task<List<byte>> ReadListByte(int messageSize);
        Task<List<float>> ReadListFloat(int messageSize);
        Task<List<string>> ReadListString(int messageSize);
        Task<List<BenchmarkGrainDataClass>> ReadListClass(int messageSize);
        Task<List<BenchmarkGrainDataStruct>> ReadListStruct(int messageSize);

        Task WriteByte(byte data);
        Task WriteFloat(float data);
        Task WriteString(string data);
        Task WriteClass(BenchmarkGrainDataClass data);
        Task WriteStruct(BenchmarkGrainDataStruct data);
        Task WriteArrayByte(byte[] data);
        Task WriteArrayFloat(float[] data);
        Task WriteArrayString(string[] data);
        Task WriteArrayClass(BenchmarkGrainDataClass[] data);
        Task WriteArrayStruct(BenchmarkGrainDataStruct[] data);
        Task WriteDictionaryByte(Dictionary<string, byte> data);
        Task WriteDictionaryFloat(Dictionary<string, float> data);
        Task WriteDictionaryString(Dictionary<string, string> data);
        Task WriteDictionaryClass(Dictionary<string, BenchmarkGrainDataClass> data);
        Task WriteDictionaryStruct(Dictionary<string, BenchmarkGrainDataStruct> data);
        Task WriteListByte(List<byte> data);
        Task WriteListFloat(List<float> data);
        Task WriteListString(List<string> data);
        Task WriteListClass(List<BenchmarkGrainDataClass> data);
        Task WriteListStruct(List<BenchmarkGrainDataStruct> data);

        //Task<double> IntergrainBenchmarkReflected(IBenchmarkGrain other, int numIterations, int numDropIterations, string functionName, object[] data);

        Task<List<TimeSpan>> ExchangeMessage(IBenchmarkGrain other, long dataLength, int numIterations);
        Task<TimeSpan> PromiseOverhead(int numIterations, int lenght, bool asynch);
    }

    public interface IThreadRingGrain : IGrain
    {
        Task SetNeighbor(IThreadRingGrain grain);
        Task SetWatcher(IThreadRingWatcher watcher);
        Task PassToken(ThreadRingToken token);
    }

    public interface IThreadRingWatcher : IGrainObserver
    {
        void FinishedTokenRing(ThreadRingToken token);
    }

    [Serializable]
    public class ThreadRingToken
    {
        public int HopCount { get; set; }
        public int HopLimit { get; set; }
        public long Owner { get; set; }
    }

    public enum DataContainerMode
    {
        ArrayMode,
        DictionaryMode,
        ListMode,
        NoContainerMode
    }

    public enum DataTypeMode
    {
        StringMode,
        FloatMode,
        ByteMode,
        ClassMode,
        StructMode
    }

    [Serializable]
    public struct BenchmarkGrainDataStruct
    {
        public float float1, float2, float3, float4;
        public int int1, int2, int3, int4;
        public string string1, string2, string3, string4;

        public BenchmarkGrainDataStruct(bool dummy)
        {
            // Can't have parameterless constructor
            float1 = 1.0f;
            float2 = 2.0f; 
            float3 = 3.0f; 
            float4 = 4.0f;
            int1 = 1; 
            int2 = 2; 
            int3 = 3; 
            int4 = 4;
            string1 = "One"; 
            string2 = "Two"; 
            string3 = "Three"; 
            string4 = "Four";
        }
    }

    [Serializable]
    public class BenchmarkGrainDataClass
    {
        public float float1 = 1.0f, float2 = 2.0f, float3 = 3.0f, float4 = 4.0f;
        public int int1 = 1, int2 = 2, int3 = 3, int4 = 4;
        public string string1 = "One", string2 = "Two", string3 = "Three", string4 = "Four";

        public static object CreateData(DataContainerMode containerMode, DataTypeMode dataTypeMode, int messageSize)
        {
            object element;
            Type dataType;
            switch (dataTypeMode)
            {
                case DataTypeMode.ByteMode:
                    dataType = typeof(byte);
                    element = new byte();
                    break;
                case DataTypeMode.FloatMode:
                    dataType = typeof(float);
                    element = 123.45678f;
                    break;
                case DataTypeMode.StringMode:
                    dataType = typeof(string);
                    element = "Hello World";
                    break;
                case DataTypeMode.ClassMode:
                    dataType = typeof(BenchmarkGrainDataClass);
                    element = new BenchmarkGrainDataClass();
                    break;
                case DataTypeMode.StructMode:
                    dataType = typeof(BenchmarkGrainDataStruct);
                    element = new BenchmarkGrainDataStruct(true);
                    break;
                default:
                    throw new Exception("Bad data type");
            }

            switch (containerMode)
            {
                case DataContainerMode.ArrayMode:
                    Array array = Array.CreateInstance(dataType, messageSize);
                    for (int i = 0; i < messageSize; i++)
                        array.SetValue(element, i);
                    return array;
                case DataContainerMode.DictionaryMode:
                    Type dictBaseType = typeof(Dictionary<,>);
                    Type dictGenericType = dictBaseType.MakeGenericType(new Type[] { typeof(string), dataType });
                    IDictionary dict = (IDictionary)Activator.CreateInstance(dictGenericType);
                    for (int i = 0; i < messageSize; i++)
                        dict.Add(i.ToString(), element);
                    return (object)dict;
                case DataContainerMode.ListMode:
                    Type listBaseType = typeof(List<>);
                    Type listGenericType = listBaseType.MakeGenericType(new Type[] { dataType });
                    IList list = (IList)Activator.CreateInstance(listGenericType);
                    for (int i = 0; i < messageSize; i++)
                        list.Add(element);
                    return (object)list;
                case DataContainerMode.NoContainerMode:
                    return element;
                default:
                    throw new Exception("Bad container type");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Placement;

namespace UnitTestGrains
{
    public interface IBenchmarkGrainState : IGrainState
    {
        string Name { get; }
        IBenchmarkGrain Other { get; set; }

        int DummyDelay { get; set; }
    }

    public class BenchmarkGrain : Grain<IBenchmarkGrainState>, IBenchmarkGrain
    {
        Task<string> IBenchmarkGrain.GetName() { return Task.FromResult(State.Name); }

        Task<IBenchmarkGrain> IBenchmarkGrain.GetOther() { return Task.FromResult(State.Other); }

        Task<int> IBenchmarkGrain.GetDummyDelay() { return Task.FromResult(State.DummyDelay); }

        private AsyncPipeline pipeline;

        public override Task OnActivateAsync()
        {
            pipeline = new AsyncPipeline(10);
            return TaskDone.Done;
        }

        public Task Ping()
        {
            return TaskDone.Done;
        }

        public Task DummyRead()
        {
            Delay();
            return TaskDone.Done;
        }

        public Task DummyWrite()
        {
            Delay();
            return TaskDone.Done;
        }

        public Task ReadOther(int count)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
                { var c = State.Other.DummyRead(); pipeline.Add(c); return c; }));
        }

        public Task WriteOther(int count)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            { var c = State.Other.DummyWrite(); pipeline.Add(c); return c; }));
        }

        public Task WriteData(object data)
        {
            return TaskDone.Done;
        }

        public Task<byte> ReadByte(int messageSize) { return Task.FromResult((byte)BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ByteMode, messageSize)); }
        public Task<float> ReadFloat(int messageSize) { return Task.FromResult((float)BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.FloatMode, messageSize)); }
        public Task<string> ReadString(int messageSize) { return Task.FromResult((string)BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StringMode, messageSize)); }
        public Task<BenchmarkGrainDataClass> ReadClass(int messageSize) { return Task.FromResult((BenchmarkGrainDataClass)BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ClassMode, messageSize)); }
        public Task<BenchmarkGrainDataStruct> ReadStruct(int messageSize) { return Task.FromResult((BenchmarkGrainDataStruct)BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StructMode, messageSize)); }
        public Task<byte[]> ReadArrayByte(int messageSize) { return Task.FromResult((byte[])BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ByteMode, messageSize)); }
        public Task<float[]> ReadArrayFloat(int messageSize) { return Task.FromResult((float[])BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.FloatMode, messageSize)); }
        public Task<string[]> ReadArrayString(int messageSize) { return Task.FromResult((string[])BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StringMode, messageSize)); }
        public Task<BenchmarkGrainDataClass[]> ReadArrayClass(int messageSize) { return Task.FromResult((BenchmarkGrainDataClass[])BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ClassMode, messageSize)); }
        public Task<BenchmarkGrainDataStruct[]> ReadArrayStruct(int messageSize) { return Task.FromResult((BenchmarkGrainDataStruct[])BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StructMode, messageSize)); }
        public Task<Dictionary<string, byte>> ReadDictionaryByte(int messageSize) { return Task.FromResult((Dictionary<string, byte>)BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ByteMode, messageSize)); }
        public Task<Dictionary<string, float>> ReadDictionaryFloat(int messageSize) { return Task.FromResult((Dictionary<string, float>)BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.FloatMode, messageSize)); }
        public Task<Dictionary<string, string>> ReadDictionaryString(int messageSize) { return Task.FromResult((Dictionary<string, string>)BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StringMode, messageSize)); }
        public Task<Dictionary<string, BenchmarkGrainDataClass>> ReadDictionaryClass(int messageSize) { return Task.FromResult((Dictionary<string, BenchmarkGrainDataClass>)BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ClassMode, messageSize)); }
        public Task<Dictionary<string, BenchmarkGrainDataStruct>> ReadDictionaryStruct(int messageSize) { return Task.FromResult((Dictionary<string, BenchmarkGrainDataStruct>)BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StructMode, messageSize)); }
        public Task<List<byte>> ReadListByte(int messageSize) { return Task.FromResult((List<byte>)BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ByteMode, messageSize)); }
        public Task<List<float>> ReadListFloat(int messageSize) { return Task.FromResult((List<float>)BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.FloatMode, messageSize)); }
        public Task<List<string>> ReadListString(int messageSize) { return Task.FromResult((List<string>)BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StringMode, messageSize)); }
        public Task<List<BenchmarkGrainDataClass>> ReadListClass(int messageSize) { return Task.FromResult((List<BenchmarkGrainDataClass>)BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ClassMode, messageSize)); }
        public Task<List<BenchmarkGrainDataStruct>> ReadListStruct(int messageSize) { return Task.FromResult((List<BenchmarkGrainDataStruct>)BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StructMode, messageSize)); }

        public Task WriteByte(byte data) { return TaskDone.Done; }
        public Task WriteFloat(float data) { return TaskDone.Done; }
        public Task WriteString(string data) { return TaskDone.Done; }
        public Task WriteClass(BenchmarkGrainDataClass data) { return TaskDone.Done; }
        public Task WriteStruct(BenchmarkGrainDataStruct data) { return TaskDone.Done; }
        public Task WriteArrayByte(byte[] data) { return TaskDone.Done; }
        public Task WriteArrayFloat(float[] data) { return TaskDone.Done; }
        public Task WriteArrayString(string[] data) { return TaskDone.Done; }
        public Task WriteArrayClass(BenchmarkGrainDataClass[] data) { return TaskDone.Done; }
        public Task WriteArrayStruct(BenchmarkGrainDataStruct[] data) { return TaskDone.Done; }
        public Task WriteDictionaryByte(Dictionary<string, byte> data) { return TaskDone.Done; }
        public Task WriteDictionaryFloat(Dictionary<string, float> data) { return TaskDone.Done; }
        public Task WriteDictionaryString(Dictionary<string, string> data) { return TaskDone.Done; }
        public Task WriteDictionaryClass(Dictionary<string, BenchmarkGrainDataClass> data) { return TaskDone.Done; }
        public Task WriteDictionaryStruct(Dictionary<string, BenchmarkGrainDataStruct> data) { return TaskDone.Done; }
        public Task WriteListByte(List<byte> data) { return TaskDone.Done; }
        public Task WriteListFloat(List<float> data) { return TaskDone.Done; }
        public Task WriteListString(List<string> data) { return TaskDone.Done; }
        public Task WriteListClass(List<BenchmarkGrainDataClass> data) { return TaskDone.Done; }
        public Task WriteListStruct(List<BenchmarkGrainDataStruct> data) { return TaskDone.Done; }


        public async Task<List<TimeSpan>> ExchangeMessage(IBenchmarkGrain other, long dataLength, int numIterations)
        {
            //logger.Info("ExchangeMessage " + dataLength + " " + numIterations);
            Stopwatch stopwatch = new Stopwatch();
            byte[] data = new byte[dataLength];
            List<TimeSpan> list = new List<TimeSpan>();
            
            await other.WriteData(data); // warm up.
            await other.WriteData(data); // warm up.

            for (int i = 0; i < numIterations; i++)
            {
                stopwatch.Start();
                await other.WriteData(data);
                // measure time until the reply arrivies, but don't use Wait() since Wait is not optimized for latency in our scheduler.
                stopwatch.Stop();
                list.Add(stopwatch.Elapsed);
                stopwatch.Reset();
            }
            return list; // stopwatch.Elapsed;
        }

        private void Delay()
        {
            var watch = new Stopwatch();
            watch.Start();
            while (watch.ElapsedMilliseconds < State.DummyDelay)
                Fibonacci(10);
            watch.Stop();
        }

        private int Fibonacci(int n)
        {
            if (n == 0) return 0;
            if (n == 1) return 1;
            return Fibonacci(n - 1) + Fibonacci(n - 2);
        }

        private int Foo(int n)
        {
            int counter = 0;
            for (int i = 0; i < n; i++)
            {
                counter += i;
            }
            return counter;
            //Thread.Sleep(n);
            //return n / 2;
        }

        public async Task<TimeSpan> PromiseOverhead(int numIterations, int lenght, bool asynch)
        {
            Stopwatch stopwatch = new Stopwatch();
            if (asynch)
            {
                for (int i = 0; i < numIterations; i++)
                {
                    stopwatch.Start();
                    await Task.Factory.StartNew(() => { Foo(lenght); });
                    stopwatch.Stop();
                }
            }
            else
            {
                for (int i = 0; i < numIterations; i++)
                {
                    stopwatch.Start();
                    Foo(lenght);
                    stopwatch.Stop();
                }
            }
            return stopwatch.Elapsed;
        }
    }

    [Reentrant]
    public class ThreadRingGrain : Grain, IThreadRingGrain
    {
        private IThreadRingGrain neighbor;
        private IThreadRingWatcher watcher;

        public Task SetNeighbor(IThreadRingGrain grain)
        {
            neighbor = grain;
            return TaskDone.Done;
        }

        public Task SetWatcher(IThreadRingWatcher observer)
        {
            watcher = observer;
            return TaskDone.Done;
        }

        public Task PassToken(ThreadRingToken token)
        {
            token.Owner = this.GetPrimaryKeyLong();
            token.HopCount++;
            if (token.HopCount == token.HopLimit)
            {
                watcher.FinishedTokenRing(token);
                return TaskDone.Done;
            }
            else
            {
                return neighbor.PassToken(token);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Concurrency;

using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    internal class SimpleSelfManagedGrain : Grain, ISimpleSelfManagedGrain
    {
        private string label;
        private Logger logger;
        private IDisposable timer; 

        public override Task OnActivateAsync()
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            logger = base.GetLogger("SimpleSelfManagedGrain " + base.Data.Address.ToString());
            label = this.GetPrimaryKeyLong().ToString();
            logger.Info("OnActivateAsync");

            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("!!! OnDeactivateAsync");
            return base.OnDeactivateAsync();
        }

        #region Implementation of ISimpleSelfManagedGrain

        public Task<long> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKeyLong());
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public async Task DoLongAction(TimeSpan timespan, string str)
        {
            logger.Info("DoLongAction {0} received", str);
            await Task.Delay(timespan);
        }

        public Task SetLabel(string label)
        {
            this.label = label;
            logger.Info("SetLabel {0} received", label);
            return TaskDone.Done;
        }

        public Task StartTimer()
        {
            logger.Info("StartTimer.");
            timer = base.RegisterTimer(TimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            
            return TaskDone.Done;
        }

        private Task TimerTick(object data)
        {
            logger.Info("TimerTick.");
            return TaskDone.Done;
        }

        public async Task<Tuple<string, string>> TestRequestContext()
        {
            string bar1 = null;
            RequestContext.Set("jarjar", "binks");

            Task task = Task.Factory.StartNew(() =>
            {
                bar1 = (string)RequestContext.Get("jarjar");
                logger.Info("bar = {0}.", bar1);
            });

            string bar2 = null;
            Task ac = Task.Factory.StartNew(() =>
            {
                bar2 = (string)RequestContext.Get("jarjar");
                logger.Info("bar = {0}.", bar2);
            });

            await Task.WhenAll(task, ac);
            return new Tuple<string, string>(bar1, bar2);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

        public Task<GrainId> GetGrainId()
        {
            return Task.FromResult(Identity);
        }

        public Task<IGrain[]> GetMultipleGrainInterfaces_Array()
        {
            IGrain[] grains = new IGrain[5];
            for (int i = 0; i < grains.Length; i++)
            {
                grains[i] = SimpleSelfManagedGrainFactory.GetGrain(i);
            }
            return Task.FromResult(grains);
        }

        public Task<List<IGrain>> GetMultipleGrainInterfaces_List()
        {
            IGrain[] grains = new IGrain[5];
            for (int i = 0; i < grains.Length; i++)
            {
                grains[i] = SimpleSelfManagedGrainFactory.GetGrain(i);
            }
            return Task.FromResult(grains.ToList());
        }

        //public Task Deactivate(long id)
        //{
        //    IAddressable sessionGrain = SimpleSelfManagedGrainFactory.GetGrain(id);
        //    return Domain.Current.DeactivateGrainsOnIdle(new List<IAddressable>() { sessionGrain });
        //}

        #endregion
    }

    public class ProxyGrain : Grain, IProxyGrain
    {
        private ISimpleSelfManagedGrain proxy;

        public Task CreateProxy(long key)
        {
            proxy = SimpleSelfManagedGrainFactory.GetGrain(key);
            return TaskDone.Done;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<string> GetProxyRuntimeInstanceId()
        {
            return proxy.GetRuntimeInstanceId();
        }
    }

    internal class StressSelfManagedGrain : Grain, IStressSelfManagedGrain
    {
        private string label;

        private Logger logger;

        public override Task OnActivateAsync()
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            logger = base.GetLogger("StressSelfManagedGrain " + base.RuntimeIdentity);
            label = this.GetPrimaryKeyLong().ToString();
            logger.Info("OnActivateAsync");

            return TaskDone.Done;
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public Task SetLabel(string label)
        {
            this.label = label;

            //logger.Info("SetLabel {0} received", label);
            return TaskDone.Done;
        }

        public Task<GrainId> GetGrainId()
        {
            return Task.FromResult(Identity);
        }

        public Task PingOthers(long[] others)
        {
            List<Task> promises = new List<Task>();
            foreach (long key in others)
            {
                IStressSelfManagedGrain g1 = StressSelfManagedGrainFactory.GetGrain(key);
                Task promise = g1.GetLabel();
                promises.Add(promise);
            }
            return Task.WhenAll(promises);
        }

        public Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(SiloAddress destination, List<Tuple<GrainId, int>> grainAndETagList, int retries = 0)
        {
            List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>> list = new List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>();
            foreach (Tuple<GrainId, int> tuple in grainAndETagList)
            {
                GrainId id = tuple.Item1;
                List<Tuple<SiloAddress, ActivationId>> reply = new List<Tuple<SiloAddress, ActivationId>>();
                for (int i = 0; i < 10; i++)
                {
                    reply.Add(new Tuple<SiloAddress, ActivationId>(SiloAddress.NewLocalAddress(0), ActivationId.NewId()));
                }
                list.Add(new Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>(id, 3, reply));
            }
            return Task.FromResult(list);
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task Send(byte[] data)
        {
            return TaskDone.Done;
        }

        public Task DeactivateSelf()
        {
            DeactivateOnIdle();
            return TaskDone.Done;
        }
    }
    
    [Reentrant]
    internal class ReentrantStressSelfManagedGrain : Grain, IReentrantStressSelfManagedGrain
    {
        private string label;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            label = this.GetPrimaryKeyLong().ToString();
            logger = base.GetLogger("ReentrantStressSelfManagedGrain " + base.Data.Address.ToString());
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<GrainId> GetGrainId()
        {
            return Task.FromResult(Identity);
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableArray(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableArray(data, -1, false);
                }
            }
            return TaskDone.Done;
        }

        public Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableArray(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableArray(data, -1, false);
                }
            }
            return TaskDone.Done;
        }

        public Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableDictionary(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableDictionary(data, -1, false);
                }
            }
            return TaskDone.Done;
        }

        public Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableDictionary(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableDictionary(data, -1, false);
                }
            }
            return TaskDone.Done;
        }

        public async Task InterleavingConsistencyTest(int numItems)
        {
            TimeSpan delay = TimeSpan.FromMilliseconds(1);
            SafeRandom random = new SafeRandom();

            List<Task> getFileMetadataPromises = new List<Task>(numItems * 2);
            Dictionary<int, string> fileMetadatas = new Dictionary<int, string>(numItems*2);

            for (int i = 0; i < numItems; i++ )
            {
                int capture = i;
                Func<Task> func = (
                    async () =>
                    {
                        await Task.Delay(random.NextTimeSpan(delay));
                        int fileMetadata = capture;
                        if ((fileMetadata%2) == 0)
                        {
                            fileMetadatas.Add(fileMetadata, fileMetadata.ToString());
                        }
                    });
                getFileMetadataPromises.Add(func());
            }

            await Task.WhenAll(getFileMetadataPromises.ToArray());

            List<Task> tagPromises = new List<Task>(fileMetadatas.Count);

            foreach (KeyValuePair<int, string> keyValuePair in fileMetadatas)
            {
                int fileId = keyValuePair.Key;
                Func<Task> func = (async () =>
                    {
                        await Task.Delay(random.NextTimeSpan(delay));
                        string fileMetadata = fileMetadatas[fileId];
                    });
                tagPromises.Add(func());
            }

            await Task.WhenAll(tagPromises);

            // sort the fileMetadatas according to fileIds.
            List<string> results = new List<string>(fileMetadatas.Count);
            for (int i = 0; i < numItems; i++)
            {
                string metadata;
                if (fileMetadatas.TryGetValue(i, out metadata))
                {
                    results.Add(metadata);
                }
            }

            if (numItems != results.Count)
            {
                //throw new OrleansException(String.Format("numItems != results.Count, {0} != {1}", numItems, results.Count));
            }

        }
    }

    [Reentrant]
    [StatelessWorker]
    public class ReentrantLocalStressSelfManagedGrain : Grain, IReentrantLocalStressSelfManagedGrain
    {
        private string label;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            label = this.GetPrimaryKeyLong().ToString();
            logger = base.GetLogger("ReentrantLocalStressSelfManagedGrain " + base.Data.Address.ToString());
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableArray(data, -1, false);
                }else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableArray(data, -1, false);
                }
            }
            return TaskDone.Done; 
        }

        public Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableArray(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableArray(data, -1, false);
                }
            }
            return TaskDone.Done; 
        }

        public Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableDictionary(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingMutableDictionary(data, -1, false);
                }
            }
            return TaskDone.Done; 
        }

        public Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return ReentrantStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableDictionary(data, -1, false);
                }
                else
                {
                    return ReentrantLocalStressSelfManagedGrainFactory.GetGrain(nextGrain).PingImmutableDictionary(data, -1, false);
                }
            }
            return TaskDone.Done; 
        }
    }

    internal class GuidSimpleSelfManagedGrain : Grain, IGuidSimpleSelfManagedGrain
    {
        private string label;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            //if (this.GetPrimaryKeyLong() == -2)
            //    throw new ArgumentException("Primary key cannot be -2 for this test case");

            label = this.GetPrimaryKey().ToString();
            logger = base.GetLogger("GuidSimpleSelfManagedGrain " + base.Data.Address.ToString());
            logger.Info("OnActivateAsync");

            return TaskDone.Done;
        }
        #region Implementation of ISimpleSelfManagedGrain

        public Task<Guid> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKey());
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public Task SetLabel(string label)
        {
            this.label = label;
            return TaskDone.Done;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

        public Task<GrainId> GetGrainId()
        {
            return Task.FromResult(Identity);
        }

        //public Task Deactivate(long id)
        //{
        //    IAddressable sessionGrain = SimpleSelfManagedGrainFactory.GetGrain(id);
        //    return Domain.Current.DeactivateGrainsOnIdle(new List<IAddressable>() { sessionGrain });
        //}

        #endregion
    }
}

using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Concurrency;
using UnitTestGrains;

namespace ProxyErrorGrain
{
    [StatelessWorker]
    public class ProxyErrorGrain : UnitTests.Grains.SimpleGrain, IProxyErrorGrain
    {
        protected  IErrorGrain Reference { get; set; }

        protected  IProxyErrorGrain ProxyReference { get; set; }

        public override Task OnActivateAsync()
        {
            logger = GetLogger(String.Format("ProxyErrorGrain-{0}-{1}", base.Identity, base.RuntimeIdentity));
            logger.Info("Activate");
            return TaskDone.Done;
        }

        public Task ConnectTo(IErrorGrain errorGrain)
        {
            Reference = errorGrain;
            //reference.StateUpdateEvent += new SimpleGrainClient.StateUpdateEventHandler(reference_StateUpdateEvent);
            return TaskDone.Done;
        }

        //void reference_StateUpdateEvent(object sender, SimpleGrainClient.StateUpdateEventArgs e)
        //{
        //    SetA(e.A);
        //    SetB(e.B);
        //    base.RaiseStateUpdateEvent();
        //}

        public async Task SetAError(int a)
        {
            logger.Info("SetAError={0}", a);
            if (Reference == null)
                throw new ApplicationException("Not connected to a ErrorGrain. Call ConnectTo first.");


            await Reference.SetAError(a);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task ConnectToProxy(IProxyErrorGrain proxy)
        {
            ProxyReference = proxy;
            return TaskDone.Done;
        }

        public Task<string> GetProxyRuntimeInstanceId()
        {
            return ProxyReference.GetRuntimeInstanceId();
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

        //public Task MoveActivation(SiloAddress fromSilo, ActivationId activationId, SiloAddress toSilo)
        //{
        //    return Silo.SystemCatalog.MoveActivation(fromSilo, activationId, toSilo);
        //}
    }
}

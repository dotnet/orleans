using System.Threading.Tasks;
using Orleans;
using UnitTestGrains;

namespace UnitTests
{
    public class SimpleGrainSmartProxy
    {
        private bool persistent;
        private IErrorPersistentGrain persistentGrain = null;
        private IErrorGrain nonPersistentGrain = null;

        public SimpleGrainSmartProxy(bool persist/*, StringGrainKey grainId*/)
        {
            persistent = persist;
            if (persistent)
            {
                persistentGrain = ErrorPersistentGrainFactory.GetGrain(UnitTestBase.GetRandomGrainId());
            }
            else
            {
                nonPersistentGrain = ErrorGrainFactory.GetGrain(UnitTestBase.GetRandomGrainId());
            }
        }
        public Task LongMethod(int waitTime)
        {
            if (persistent)
            {
                return persistentGrain.LongMethod(waitTime);
            }
            else
            {
                return nonPersistentGrain.LongMethod(waitTime);
            }
        }

        public Task LogMessage(string msg)
        {
            if (persistent)
            {
                return persistentGrain.LogMessage(msg);
            }
            else
            {
                return nonPersistentGrain.LogMessage(msg);
            }
        }

        public Task SetA(int a)
        {
            if (persistent)
            {
                return persistentGrain.SetA(a);
            }
            else
            {
                return nonPersistentGrain.SetA(a);
            }
        }
        public Task IncrementA()
        {
            if (persistent)
            {
                return persistentGrain.IncrementA();
            }
            else
            {
                return nonPersistentGrain.IncrementA();
            }
        }
        public Task<int> GetA()
        {
            if (persistent)
            {
                return persistentGrain.GetA();
            }
            else
            {
                return nonPersistentGrain.GetA();
            }
        }
    }
}

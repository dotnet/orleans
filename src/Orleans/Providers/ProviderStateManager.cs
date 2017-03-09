using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers
{
    internal class ProviderStateManager
    {
        public ProviderState State { get; private set; }
        private ProviderState presetState;

        public ProviderStateManager()
        {
            State = ProviderState.None;
        }

        public bool PresetState(ProviderState state)
        {
            presetState = state;
            switch (state)
            {
                case ProviderState.None:
                    throw new ProviderStateException("Provider state can not be set to none.");

                case ProviderState.Initialized:
                    switch(State)
                    {
                        case ProviderState.None:
                            return true;
                    }
                    break;

                case ProviderState.Started:
                    switch(State)
                    {
                        case ProviderState.None:
                            throw new ProviderStateException("Trying to start a provider that hasn't been initialized.");
                        case ProviderState.Initialized:
                            return true;
                        case ProviderState.Closed:
                            throw new ProviderStateException("Trying to start a provider that has been closed.");
                    }
                    break;

                case ProviderState.Closed:
                    switch (State)
                    {
                        case ProviderState.None:
                            throw new ProviderStateException("Trying to close a provider that hasn't been initialized.");
                        case ProviderState.Initialized:
                        case ProviderState.Started:
                            return true;
                    }
                    return true;
            }

            return false;
        }

        public void CommitState()
        {
            State = presetState;
        }
    }
}

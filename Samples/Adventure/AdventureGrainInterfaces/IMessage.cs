using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AdventureGrainInterfaces
{
    public interface IMessage : IGrainObserver
    {
        void ReceiveMessage(string message);

    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionManagerRetriever
    {
        IStreamSubscriptionManager GetStreamSubscriptionManager();
    }
}

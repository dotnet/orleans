﻿using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionObserver<T> 
    {
        Task OnSubscribed(StreamSubscriptionHandle<T> handle);
    }
}

﻿using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers.Testing
{
    /// <summary>
    /// NoOpCheckpointer is used in EventDataGeneratorStreamProvider eco system to replace the default Checkpointer which requires a back end storage. In EventHubDataGeneratorStreamProvider,
    /// it is generating EventData on the fly when receiver pull messages from the queue, which means it doesn't support recoverable stream, hence check pointing won't bring much value there. 
    /// So a checkpointer with no ops should be enough.
    /// </summary>
    public class NoOpCheckpointer : IStreamQueueCheckpointer<string>
    {
        public static NoOpCheckpointer Instance = new NoOpCheckpointer();

        public bool CheckpointExists => true;
        public Task<string> Load()
        {
            return Task.FromResult(EventHubConstants.StartOfStream);
        }
        public void Update(string offset, DateTime utcNow)
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Streams.Filtering
{
    public interface IStreamFilter
    {
        bool ShouldDeliver(StreamId streamId, object item, string filterData);
    }

    internal class NoOpStreamFilter : IStreamFilter
    {
        public bool ShouldDeliver(StreamId streamId, object item, string filterData) => true;
    }
}

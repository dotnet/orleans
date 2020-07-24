using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class DefaultStreamFilterPredicateWrapper : IStreamFilterPredicateWrapper
    {
        public object FilterData { get { return default(object); } }
        public bool ShouldReceive(StreamId stream, object filterData, object item)
        {
            return true;
        }
    }
}

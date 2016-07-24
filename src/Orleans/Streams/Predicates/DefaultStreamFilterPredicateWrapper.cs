namespace Orleans.Streams
{
    internal class DefaultStreamFilterPredicateWrapper : IStreamFilterPredicateWrapper
    {
        public object FilterData { get { return default(object); } }
        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            return true;
        }
    }
}

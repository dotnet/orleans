namespace Orleans.Streams
{
    public delegate bool StreamFilterPredicate(IStreamIdentity stream, object filterData, object item);
}
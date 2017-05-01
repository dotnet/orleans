namespace Orleans.Streams
{
    public interface IStreamNamespacePredicate
    {
        bool IsMatch(string streamNamespace);
    }
}
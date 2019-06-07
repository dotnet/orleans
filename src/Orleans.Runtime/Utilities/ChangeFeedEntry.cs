using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public abstract class ChangeFeedEntry<T>
    {
        public abstract bool HasValue { get; }

        public abstract T Value { get; }

        public abstract Task<ChangeFeedEntry<T>> NextAsync();
    }
}

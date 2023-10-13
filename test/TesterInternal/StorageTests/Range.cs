using System.Diagnostics;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Implements the concept of a contiguous range.
    /// </summary>
    /// <typeparam name="T">The type of the range.</typeparam>
    /// <remarks>A rudimentary implementation.</remarks>
    [DebuggerDisplay("Start = {Start}, End = {End}")]
    public sealed class Range<T>: IEquatable<Range<T>>
    {
        /// <summary>
        /// The start of a contiguous range.
        /// </summary>
        public T Start { get; }

        /// <summary>
        /// The end of a contiguous range.
        /// </summary>
        public T End { get; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="start">The start of the range.</param>
        /// <param name="end">The end of the range.</param>
        /// <param name="comparer">The range comparer.</param>
        /// <exception cref="ArgumentOutOfRangeException"/>.
        public Range(T start, T end, IComparer<T> comparer = null)
        {
            var comp = comparer == null ? Comparer<T>.Default : comparer;
            if(comp.Compare(end, start) < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(end), end, $"The relation min ({start}) <= max ({end}) must hold.");
            }

            Start = start;
            End = end;
        }


        public override bool Equals(object obj)
        {
            if(!(obj is Range<T>)) return false;
            Range<T> other = (Range<T>)obj;

            return Equals(other);
        }


        public bool Equals(Range<T> other)
        {
            return Start.Equals(other.Start) && End.Equals(other.End);
        }


        public override int GetHashCode()
        {
            unchecked
            {
                var comparer = EqualityComparer<T>.Default;
                int hash = 17;
                hash = hash * 23 + comparer.GetHashCode(Start);
                hash = hash * 23 + comparer.GetHashCode(End);

                return hash;
            }
        }
    }
}

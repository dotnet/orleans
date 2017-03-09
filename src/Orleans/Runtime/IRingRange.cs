namespace Orleans.Runtime
{
    public interface IRingRange
    {
        /// <summary>
        /// Check if <paramref name="n"/> is our responsibility to serve
        /// </summary>
        /// <returns>true if the reminder is in our responsibility range, false otherwise</returns>
        bool InRange(uint n);

        bool InRange(GrainReference grainReference);
    }
}
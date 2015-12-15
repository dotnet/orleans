namespace Orleans.Streams
{
    /// <summary>
    /// Filter predicate for streams. 
    /// Classes implementing this interface MUST be [Serializable]
    /// </summary>
    internal interface IStreamFilterPredicateWrapper
    {
        object FilterData { get; }

        /// <summary>
        /// Should this item be delivered to the intended receiver?
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="filterData"></param>
        /// <param name="item">Item sent through the stream.</param>
        /// <returns>Return <c>true</c> if this item should be delivered to the intended recipient.</returns>
        bool ShouldReceive(IStreamIdentity stream, object filterData, object item);
    }
}

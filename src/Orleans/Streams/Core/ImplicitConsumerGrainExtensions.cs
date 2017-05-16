namespace Orleans.Streams
{
    /// <summary>
    /// Extension methods for grains implicitly subscribed to streams.
    /// </summary>
    public static class ImplicitConsumerGrainExtensions
    {
        /// <summary>
        /// Constructs <see cref="StreamIdentity"/> of the stream that the grain is implicitly subscribed to.
        /// </summary>
        /// <param name="grain">The implicitly subscribed grain.</param>
        /// <returns>The stream identity (key + namespace).</returns>
        public static StreamIdentity GetImplicitStreamIdentity(this IGrainWithGuidCompoundKey grain)
        {
            string keyExtension;
            var key = grain.GetPrimaryKey(out keyExtension);
            return new StreamIdentity(key, keyExtension);
        }
    }
}
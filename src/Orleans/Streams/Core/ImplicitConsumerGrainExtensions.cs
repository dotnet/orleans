namespace Orleans.Streams
{
    public static class ImplicitConsumerGrainExtensions
    {
        public static StreamIdentity GetImplicitStreamIdentity(this IGrainWithGuidCompoundKey grain)
        {
            string keyExtension;
            var key = grain.GetPrimaryKey(out keyExtension);
            return new StreamIdentity(key, keyExtension);
        }
    }
}
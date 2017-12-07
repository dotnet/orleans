using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal static class GrainIdExtensions
    {
        public static GrainId FromByteArray(byte[] byteArray)
        {
            var reader = new BinaryTokenStreamReader(byteArray);
            return reader.ReadGrainId();
        }

        public static byte[] ToByteArray(this GrainId @this)
        {
            var writer = new BinaryTokenStreamWriter();
            writer.Write(@this);
            var result = writer.ToByteArray();
            writer.ReleaseBuffers();
            return result;
        }
    }
}

using System;
using System.Globalization;
using System.Text;


namespace Orleans.Storage
{
    /// <summary>
    /// This is an internal helper class that collects grain key information
    /// so that's easier to manage during database operations.
    /// </summary>
    internal class AdoGrainKey
    {
        public long N0Key { get; }

        public long N1Key { get; }

        public string StringKey { get; }

        public bool IsLongKey { get; }

        public bool IsGuidKey { get; }

        public bool IsStringKey { get; }

        public AdoGrainKey(long key, string keyExtension)
        {
            N0Key = 0;
            N1Key = key;
            StringKey = keyExtension;

            IsLongKey = true;
            IsGuidKey = false;
            IsStringKey = false;
        }

        public AdoGrainKey(Guid key, string keyExtension)
        {
            var guidKeyBytes = key.ToByteArray();
            N0Key = BitConverter.ToInt64(guidKeyBytes, 0);
            N1Key = BitConverter.ToInt64(guidKeyBytes, 8);
            StringKey = keyExtension;

            IsLongKey = false;
            IsGuidKey = true;
            IsStringKey = false;
        }

        public AdoGrainKey(string key)
        {
            StringKey = key;
            N0Key = 0;
            N1Key = 0;

            IsLongKey = false;
            IsGuidKey = false;
            IsStringKey = true;
        }

        public byte[] GetHashBytes()
        {
            byte[] bytes = null;
            if(IsLongKey)
            {
                bytes = BitConverter.GetBytes(N1Key);
            }
            else if(IsGuidKey)
            {
                bytes = ToGuidKey(N0Key, N1Key).ToByteArray();
            }

            if(bytes != null && StringKey != null)
            {
                int oldLen = bytes.Length;
                var stringBytes = Encoding.UTF8.GetBytes(StringKey);
                Array.Resize(ref bytes, bytes.Length + stringBytes.Length);
                Array.Copy(stringBytes, 0, bytes, oldLen, stringBytes.Length);
            }

            if(bytes == null)
            {
                bytes = Encoding.UTF8.GetBytes(StringKey);
            }

            if(BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        public override string ToString()
        {
            string primaryKey;
            string keyExtension = null;
            if(IsLongKey)
            {
                primaryKey = N1Key.ToString(CultureInfo.InvariantCulture);
                keyExtension = StringKey;
            }
            else if(IsGuidKey)
            {
                primaryKey = ToGuidKey(N0Key, N1Key).ToString();
                keyExtension = StringKey;
            }
            else
            {
                primaryKey = StringKey;
            }

            const string GrainIdAndExtensionSeparator = "#";
            return string.Format($"{primaryKey}{(keyExtension != null ? GrainIdAndExtensionSeparator + keyExtension : string.Empty)}");
        }

        private static Guid ToGuidKey(long n0Key, long n1Key)
        {
            return new Guid((uint)(n0Key & 0xffffffff), (ushort)(n0Key >> 32), (ushort)(n0Key >> 48), (byte)n1Key, (byte)(n1Key >> 8), (byte)(n1Key >> 16), (byte)(n1Key >> 24), (byte)(n1Key >> 32), (byte)(n1Key >> 40), (byte)(n1Key >> 48), (byte)(n1Key >> 56));
        }
    }
}

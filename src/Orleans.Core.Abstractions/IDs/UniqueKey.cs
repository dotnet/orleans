using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Orleans.Runtime
{
    [Serializable]
    internal class UniqueKey : IComparable<UniqueKey>, IEquatable<UniqueKey>
    {
        private const ulong TYPE_CODE_DATA_MASK = 0xFFFFFFFF; // Lowest 4 bytes
        private static readonly char[] KeyExtSeparationChar = {'+'};

        /// <summary>
        /// Type id values encoded into UniqueKeys
        /// </summary>
        public enum Category : byte
        {
            None = 0,
            SystemTarget = 1,
            SystemGrain = 2,
            Grain = 3,
            Client = 4,
            KeyExtGrain = 6,
            GeoClient = 7,
        }

        public UInt64 N0 { get; private set; }
        public UInt64 N1 { get; private set; }
        public UInt64 TypeCodeData { get; private set; }
        public string KeyExt { get; private set; }

        [NonSerialized]
        private uint uniformHashCache;

        public int BaseTypeCode
        {
            get { return (int)(TypeCodeData & TYPE_CODE_DATA_MASK); }
        }

        public Category IdCategory
        {
            get { return GetCategory(TypeCodeData); }
        }

        public bool IsLongKey
        {
            get { return N0 == 0; }
        }

        public bool IsSystemTargetKey
        {
            get { return IdCategory == Category.SystemTarget; }
        }

        public bool HasKeyExt
        {
            get {
                var category = IdCategory;
                return category == Category.KeyExtGrain       
                    || category == Category.GeoClient; // geo clients use the KeyExt string to specify the cluster id
            }
        }

        internal static readonly UniqueKey Empty =
            new UniqueKey
            {
                N0 = 0,
                N1 = 0,
                TypeCodeData = 0,
                KeyExt = null
            };

        internal static UniqueKey Parse(string input)
        {
            var trimmed = input.Trim();

            // first, for convenience we attempt to parse the string using GUID syntax. this is needed by unit
            // tests but i don't know if it's needed for production.
            Guid guid;
            if (Guid.TryParse(trimmed, out guid))
                return NewKey(guid);
            else
            {
                var fields = trimmed.Split(KeyExtSeparationChar, 2);
                var n0 = ulong.Parse(fields[0].Substring(0, 16), NumberStyles.HexNumber);
                var n1 = ulong.Parse(fields[0].Substring(16, 16), NumberStyles.HexNumber);
                var typeCodeData = ulong.Parse(fields[0].Substring(32, 16), NumberStyles.HexNumber);
                string keyExt = null;
                switch (fields.Length)
                {
                    default:
                        throw new InvalidDataException("UniqueKey hex strings cannot contain more than one + separator.");
                    case 1:
                        break;
                    case 2:
                        if (fields[1] != "null")
                        {
                            keyExt = fields[1];
                        }
                        break;
                }
                return NewKey(n0, n1, typeCodeData, keyExt);
            }
        }

        private static UniqueKey NewKey(ulong n0, ulong n1, Category category, long typeData, string keyExt)
        {
            if (category != Category.KeyExtGrain && category != Category.GeoClient && keyExt != null)
                throw new ArgumentException("Only key extended grains can specify a non-null key extension.");

            var typeCodeData = ((ulong)category << 56) + ((ulong)typeData & 0x00FFFFFFFFFFFFFF);

            return NewKey(n0, n1, typeCodeData, keyExt);
        }

        internal static UniqueKey NewKey(long longKey, Category category = Category.None, long typeData = 0, string keyExt = null)
        {
            ThrowIfIsSystemTargetKey(category);

            var n1 = unchecked((ulong)longKey);
            return NewKey(0, n1, category, typeData, keyExt);
        }

        public static UniqueKey NewKey()
        {
            return NewKey(Guid.NewGuid());
        }

        internal static UniqueKey NewKey(Guid guid, Category category = Category.None, long typeData = 0, string keyExt = null)
        {
            ThrowIfIsSystemTargetKey(category);

            var guidBytes = guid.ToByteArray();
            var n0 = BitConverter.ToUInt64(guidBytes, 0);
            var n1 = BitConverter.ToUInt64(guidBytes, 8);
            return NewKey(n0, n1, category, typeData, keyExt);
        }

        public static UniqueKey NewSystemTargetKey(Guid guid, long typeData)
        {
            var guidBytes = guid.ToByteArray();
            var n0 = BitConverter.ToUInt64(guidBytes, 0);
            var n1 = BitConverter.ToUInt64(guidBytes, 8);
            return NewKey(n0, n1, Category.SystemTarget, typeData, null);
        }

        public static UniqueKey NewSystemTargetKey(short systemId)
        {
            ulong n1 = unchecked((ulong)systemId);
            return NewKey(0, n1, Category.SystemTarget, 0, null);
        }

        public static UniqueKey NewGrainServiceKey(short key, long typeData)
        {
            ulong n1 = unchecked((ulong)key);
            return NewKey(0, n1, Category.SystemTarget, typeData, null);
        }

        internal static UniqueKey NewKey(ulong n0, ulong n1, ulong typeCodeData, string keyExt)
        {
            ValidateKeyExt(keyExt, typeCodeData);
            return
                new UniqueKey
                {
                    N0 = n0,
                    N1 = n1,
                    TypeCodeData = typeCodeData,
                    KeyExt = keyExt
                };
        }

        private void ThrowIfIsNotLong()
        {
            if (!IsLongKey)
                throw new InvalidOperationException("this key cannot be interpreted as a long value");
        }

        private static void ThrowIfIsSystemTargetKey(Category category)
        {
            if (category == Category.SystemTarget)
                throw new ArgumentException(
                    "This overload of NewKey cannot be used to construct an instance of UniqueKey containing a SystemTarget id.");
        }

        private void ThrowIfHasKeyExt(string methodName)
        {
            if (HasKeyExt)
                throw new InvalidOperationException(
                    string.Format(
                        "This overload of {0} cannot be used if the grain uses the primary key extension feature.",
                        methodName));
        }

        public long PrimaryKeyToLong(out string extendedKey)
        {
            ThrowIfIsNotLong();

            extendedKey = this.KeyExt;
            return unchecked((long)N1);
        }

        public long PrimaryKeyToLong()
        {
            ThrowIfHasKeyExt("UniqueKey.PrimaryKeyToLong");
            string unused;
            return PrimaryKeyToLong(out unused);
        }

        public Guid PrimaryKeyToGuid(out string extendedKey)
        {
            extendedKey = this.KeyExt;
            return ConvertToGuid();
        }

        public Guid PrimaryKeyToGuid()
        {
            ThrowIfHasKeyExt("UniqueKey.PrimaryKeyToGuid");
            string unused;
            return PrimaryKeyToGuid(out unused);
        }

        public string ClusterId
        {
            get
            {
                if (IdCategory != Category.GeoClient)
                    throw new InvalidOperationException("ClusterId is only defined for geo clients");
                return this.KeyExt;
            }
        }

        public override bool Equals(object o)
        {
            return o is UniqueKey && Equals((UniqueKey)o);
        }

        // We really want Equals to be as fast as possible, as a minimum cost, as close to native as possible.
        // No function calls, no boxing, inline.
        public bool Equals(UniqueKey other)
        {
            return N0 == other.N0
                   && N1 == other.N1
                   && TypeCodeData == other.TypeCodeData
                   && (!HasKeyExt || KeyExt == other.KeyExt);
        }

        // We really want CompareTo to be as fast as possible, as a minimum cost, as close to native as possible.
        // No function calls, no boxing, inline.
        public int CompareTo(UniqueKey other)
        {
            return TypeCodeData < other.TypeCodeData ? -1
               : TypeCodeData > other.TypeCodeData ? 1
               : N0 < other.N0 ? -1
               : N0 > other.N0 ? 1
               : N1 < other.N1 ? -1
               : N1 > other.N1 ? 1
               : !HasKeyExt || KeyExt == null ? 0
               : String.Compare(KeyExt, other.KeyExt, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            return unchecked((int)GetUniformHashCode());
        }

        internal uint GetUniformHashCode()
        {
            // Disabling this ReSharper warning; hashCache is a logically read-only variable, so accessing them in GetHashCode is safe.
            // ReSharper disable NonReadonlyFieldInGetHashCode
            if (uniformHashCache == 0)
            {
                uint n;
                if (HasKeyExt && KeyExt != null)
                {
                    n = JenkinsHash.ComputeHash(this.ToByteArray());
                }
                else
                {
                    n = JenkinsHash.ComputeHash(TypeCodeData, N0, N1);
                }
                // Unchecked is required because the Jenkins hash is an unsigned 32-bit integer, 
                // which we need to convert to a signed 32-bit integer.
                uniformHashCache = n;
            }
            return uniformHashCache;
            // ReSharper restore NonReadonlyFieldInGetHashCode
        }

        internal byte[] ToByteArray()
        {
            byte[] bytes, extBytes = null;
            var tmpArray = new ulong[1];
            var offset = 0;
            if (this.KeyExt != null)
            {
                extBytes = Encoding.UTF8.GetBytes(KeyExt);
                // N0 + N1 + TypeCodeData + length(KeyExt in bytes) + KeyExt in bytes
                bytes = new byte[sizeof(ulong) * 3 + sizeof(int) + extBytes.Length];
            }
            else
            {
                // N0 + N1 + TypeCodeData + length(-1)
                bytes = new byte[sizeof(ulong) * 3 + sizeof(int)];
            }
            // Copy N0
            tmpArray[0] = this.N0;
            Buffer.BlockCopy(tmpArray, 0, bytes, offset, sizeof(ulong));
            offset += sizeof(ulong);
            // Copy N1
            tmpArray[0] = this.N1;
            Buffer.BlockCopy(tmpArray, 0, bytes, offset, sizeof(ulong));
            offset += sizeof(ulong);
            // Copy TypeCodeData
            tmpArray[0] = this.TypeCodeData;
            Buffer.BlockCopy(tmpArray, 0, bytes, offset, sizeof(ulong));
            offset += sizeof(ulong);
            // Copy KeyExt
            if (extBytes != null)
            {
                Buffer.BlockCopy(new[] {extBytes.Length}, 0, bytes, offset, sizeof(int));
                offset += sizeof(int);
                Buffer.BlockCopy(extBytes, 0, bytes, offset, extBytes.Length);
            }
            else
            {
                Buffer.BlockCopy(new[] {-1}, 0, bytes, offset, sizeof(int));
            }

            return bytes;
        }

        private Guid ConvertToGuid()
        {
            return new Guid((UInt32)(N0 & 0xffffffff), (UInt16)(N0 >> 32), (UInt16)(N0 >> 48), (byte)N1, (byte)(N1 >> 8), (byte)(N1 >> 16), (byte)(N1 >> 24), (byte)(N1 >> 32), (byte)(N1 >> 40), (byte)(N1 >> 48), (byte)(N1 >> 56));
        }

        public override string ToString()
        {
            return ToHexString();
        }

        internal string ToHexString()
        {
            var s = new StringBuilder();
            s.AppendFormat("{0:x16}{1:x16}{2:x16}", N0, N1, TypeCodeData);
            if (!HasKeyExt) return s.ToString();

            s.Append("+");
            s.Append(KeyExt ?? "null");
            return s.ToString();
        }

        private static void ValidateKeyExt(string keyExt, UInt64 typeCodeData)
        {
            Category category = GetCategory(typeCodeData);
            if (category == Category.KeyExtGrain)
            {
                if (string.IsNullOrWhiteSpace(keyExt))
                {
                    if (null == keyExt)
                    {
                        throw new ArgumentNullException("keyExt");

                    }
                    else
                    {
                        throw new ArgumentException("Extended key is empty or white space.", "keyExt");
                    }
                }
            }
            else if (category != Category.GeoClient && null != keyExt)
            {
                throw new ArgumentException("Extended key field is not null in non-extended UniqueIdentifier.");
            }
        }

        private static Category GetCategory(UInt64 typeCodeData)
        {
            return (Category)((typeCodeData >> 56) & 0xFF);
        }
    }
}

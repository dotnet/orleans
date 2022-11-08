using System;
using System.Net;
using Orleans.Serialization.Invocation;

namespace Orleans.Serialization.Configuration
{
    internal class DefaultTypeManifestProvider : ITypeManifestProvider
    {
        public void Configure(TypeManifestOptions typeManifest)
        {
            var wellKnownTypes = typeManifest.WellKnownTypeIds;
            wellKnownTypes[0] = typeof(void); // Represents the type of null
            wellKnownTypes[1] = typeof(int);
            wellKnownTypes[2] = typeof(string);
            wellKnownTypes[3] = typeof(bool);
            wellKnownTypes[4] = typeof(short);
            wellKnownTypes[5] = typeof(long);
            wellKnownTypes[6] = typeof(sbyte);
            wellKnownTypes[7] = typeof(uint);
            wellKnownTypes[8] = typeof(ushort);
            wellKnownTypes[9] = typeof(ulong);
            wellKnownTypes[10] = typeof(byte);
            wellKnownTypes[11] = typeof(float);
            wellKnownTypes[12] = typeof(double);
            wellKnownTypes[13] = typeof(decimal);
            wellKnownTypes[14] = typeof(char);
            wellKnownTypes[15] = typeof(Guid);
            wellKnownTypes[16] = typeof(DateTime);
            wellKnownTypes[17] = typeof(TimeSpan);
            wellKnownTypes[18] = typeof(DateTimeOffset);
            wellKnownTypes[19] = typeof(object);
            wellKnownTypes[20] = typeof(DotNetSerializableCodec);
            wellKnownTypes[21] = typeof(ExceptionCodec);
            wellKnownTypes[22] = typeof(byte[]);
            wellKnownTypes[23] = typeof(object[]);
            wellKnownTypes[24] = typeof(char[]);
            wellKnownTypes[25] = typeof(int[]);
            wellKnownTypes[26] = typeof(string[]);
            wellKnownTypes[27] = typeof(Type);
            wellKnownTypes[28] = typeof(DateOnly);
            wellKnownTypes[29] = typeof(TimeOnly);
            wellKnownTypes[30] = typeof(DayOfWeek);
            wellKnownTypes[31] = typeof(Uri);
            wellKnownTypes[32] = typeof(Version);
            wellKnownTypes[33] = typeof(IPAddress);
            wellKnownTypes[34] = typeof(IPEndPoint);
            wellKnownTypes[35] = typeof(ExceptionResponse);
            wellKnownTypes[36] = typeof(CompletedResponse);
            wellKnownTypes[37] = typeof(Int128);
            wellKnownTypes[38] = typeof(UInt128);
            wellKnownTypes[39] = typeof(Half);

            var allowedTypes = typeManifest.AllowedTypes;
            allowedTypes.Add("System.Globalization.CompareOptions");
        }
    }
}
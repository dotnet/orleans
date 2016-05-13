using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public enum ReturnCode
    {
        OK = 0,
        Fail = 1
    }

    [Serializable]
    public struct MemberVariables
    {
        public byte[] byteArray;
        public string stringVar;
        public ReturnCode code;

        public MemberVariables(byte[] bytes, string str, ReturnCode codeInput)
        {
            byteArray = bytes;
            stringVar = str;
            code = codeInput;
        }
    }

    public interface IGeneratorTestGrain : IGrainWithIntegerKey
    {
        Task<byte[]> ByteSet(byte[] data);
        Task StringSet(string str);
        Task<bool> StringIsNullOrEmpty();
        Task<MemberVariables> GetMemberVariables();
        Task SetMemberVariables(MemberVariables x);

    }
}

﻿using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GeneratorTestGrain : Grain, IGeneratorTestGrain
    {
        protected byte[] myGrainBytes;
        protected string myGrainString = string.Empty;
        protected ReturnCode myCode;

        public Task<byte[]> ByteSet(byte[] data)
        {
            myGrainBytes = (byte[])data.Clone();
            //RaiseStateUpdateEvent();
            return Task.FromResult(myGrainBytes);
        }

        public Task StringSet(string str)
        {
            myGrainString = str;
            //RaiseStateUpdateEvent();
            return TaskDone.Done;
        }

        public Task<bool> StringIsNullOrEmpty()
        {
            return Task.FromResult(String.IsNullOrEmpty(myGrainString));
        }

        public Task<MemberVariables> GetMemberVariables()
        {
            MemberVariables memberVar = new MemberVariables(myGrainBytes, myGrainString, myCode);
            return Task.FromResult(memberVar);
        }

        public Task SetMemberVariables(MemberVariables x)
        {
            myGrainBytes = (byte[])x.byteArray.Clone();
            myGrainString = x.stringVar;
            myCode = x.code;
            //RaiseStateUpdateEvent();
            return TaskDone.Done;
        }
    }
}

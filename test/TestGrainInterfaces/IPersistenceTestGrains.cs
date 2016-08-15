using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

// ReSharper disable InconsistentNaming

namespace UnitTests.GrainInterfaces
{
    public interface IPersistenceTestGrain : IGrainWithGuidKey
    {
        Task<bool> CheckStateInit();
        Task<string> CheckProviderType();
        Task DoSomething();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task<int> GetValue();
        Task DoDelete();
    }


    public interface IPersistenceTestGenericGrain<T> : IPersistenceTestGrain // IGrainWithGuidKey
    { }
    //    Task<bool> CheckStateInit();
    //    Task<string> CheckProviderType();
    //    Task DoSomething();
    //    Task DoWrite(int val);
    //    Task<int> DoRead();
    //    Task<int> GetValue();
    //    Task DoDelete();
    //}

    public interface IMemoryStorageTestGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageTestGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageGenericGrain<T> : IGrainWithIntegerKey
    {
        Task<T> GetValue();
        Task DoWrite(T val);
        Task<T> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageTestGrain_GuidExtendedKey : IGrainWithGuidCompoundKey
    {
        Task<string> GetExtendedKeyValue();
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageTestGrain_LongKey : IGrainWithIntegerKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageTestGrain_LongExtendedKey : IGrainWithIntegerCompoundKey
    {
        Task<string> GetExtendedKeyValue();
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAWSStorageTestGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAWSStorageGenericGrain<T> : IGrainWithIntegerKey
    {
        Task<T> GetValue();
        Task DoWrite(T val);
        Task<T> DoRead();
        Task DoDelete();
    }

    public interface IAWSStorageTestGrain_GuidExtendedKey : IGrainWithGuidCompoundKey
    {
        Task<string> GetExtendedKeyValue();
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAWSStorageTestGrain_LongKey : IGrainWithIntegerKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAWSStorageTestGrain_LongExtendedKey : IGrainWithIntegerCompoundKey
    {
        Task<string> GetExtendedKeyValue();
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IPersistenceErrorGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task DoWriteError(int val, bool errorBeforeWrite);
        Task<int> DoRead();
        Task<int> DoReadError(bool errorBeforeRead);
    }

    public interface IPersistenceProviderErrorGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
    }

    public interface IPersistenceUserHandledErrorGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task DoWrite(int val, bool recover);
        Task<int> DoRead(bool recover);
    }

    public interface IBadProviderTestGrain : IGrainWithGuidKey
    {
        Task DoSomething();
    }

    public interface IPersistenceNoStateTestGrain : IGrainWithGuidKey
    {
        Task DoSomething();
    }

    public interface IUser : IGrainWithGuidKey
    {
        Task<string> GetName();
        Task<string> GetStatus();

        Task UpdateStatus(string status);
        Task SetName(string name);
        Task AddFriend(IUser friend);
        Task<List<IUser>> GetFriends();
        Task<string> GetFriendsStatuses();
    }

    public interface IReentrentGrainWithState : IGrainWithGuidKey
    {
        Task Setup(IReentrentGrainWithState other);
        Task Test1();
        Task Test2();
        Task SetOne(int val);
        Task SetTwo(int val);
        Task Task_Delay(bool doStart);
    }

    public interface INonReentrentStressGrainWithoutState : IGrainWithGuidKey
    {
        Task Test1();
        Task Task_Delay(bool doStart);
    }

    public interface IInternalGrainWithState : IGrainWithIntegerKey
    {
        Task SetOne(int val);
    }

    public interface IStateInheritanceTestGrain : IGrainWithGuidKey
    {
        Task<int> GetValue();
        Task SetValue(int val);
    }

    public interface ISerializationTestGrain : IGrainWithGuidKey
    {
        Task Test_Serialize_Func();
        Task Test_Serialize_Predicate();
        Task Test_Serialize_Predicate_Class();
        Task Test_Serialize_Predicate_Class_Param(IMyPredicate pred);
    }

    public interface IMyPredicate
    {
        bool FilterFunc(int i);
    }

    [Serializable]
    public class MyPredicate : IMyPredicate
    {
        private readonly int filterValue;

        public MyPredicate(int filter)
        {
            this.filterValue = filter;
        }

        public bool FilterFunc(int i)
        {
            return i == filterValue;

        }
    }
}
// ReSharper restore InconsistentNaming

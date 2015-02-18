using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

// ReSharper disable InconsistentNaming

namespace UnitTestGrainInterfaces
{
    public interface IPersistenceTestGrain : IGrain
    {
        Task<bool> CheckStateInit();
        Task<string> CheckProviderType();
        Task DoSomething();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task<int> GetValue();
        Task DoDelete();
    }

    public interface IMemoryStorageTestGrain : IGrain
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
    }

    public interface IAzureStorageTestGrain : IGrain
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageGenericGrain<T> : IGrain
    {
        Task<T> GetValue();
        Task DoWrite(T val);
        Task<T> DoRead();
        Task DoDelete();
    }

    [ExtendedPrimaryKey]
    public interface IAzureStorageTestGrain_GuidExtendedKey : IGrain
    {
        Task<string> GetExtendedKeyValue();
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IAzureStorageTestGrain_LongKey : IGrain
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    [ExtendedPrimaryKey]
    public interface IAzureStorageTestGrain_LongExtendedKey : IGrain
    {
        Task<string> GetExtendedKeyValue();
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
        Task DoDelete();
    }

    public interface IPersistenceErrorGrain : IGrain
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task DoWriteError(int val, bool errorBeforeWrite);
        Task<int> DoRead();
        Task<int> DoReadError(bool errorBeforeRead);
    }

    public interface IPersistenceProviderErrorGrain : IGrain
    {
        Task<int> GetValue();
        Task DoWrite(int val);
        Task<int> DoRead();
    }

    public interface IPersistenceUserHandledErrorGrain : IGrain
    {
        Task<int> GetValue();
        Task DoWrite(int val, bool recover);
        Task<int> DoRead(bool recover);
    }

    public interface IBadProviderTestGrain : IGrain
    {
        Task DoSomething();
    }

    public interface IPersistenceNoStateTestGrain : IGrain
    {
        Task DoSomething();
    }

    public interface IUser : IGrain
    {
        Task<string> GetName();
        Task<string> GetStatus();

        Task UpdateStatus(string status);
        Task SetName(string name);
        Task AddFriend(IUser friend);
        Task<List<IUser>> GetFriends();
        Task<string> GetFriendsStatuses();
    }

    public interface IReentrentGrainWithState : IGrain
    {
        Task Setup(IReentrentGrainWithState other);
        Task Test1();
        Task Test2();
        Task SetOne(int val);
        Task SetTwo(int val);
        Task Task_Delay(bool doStart);
    }

    public interface INonReentrentStressGrainWithoutState : IGrain
    {
        Task Test1();
        Task Task_Delay(bool doStart);
    }

    internal interface IInternalGrainWithState : IGrain
    {
        Task SetOne(int val);
    }

    public interface IStateInheritanceTestGrain : IGrain
    {
        Task<int> GetValue();
        Task SetValue(int val);
    }

    public interface ISerializationTestGrain : IGrain
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

using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;

namespace UnitTests.GrainInterfaces
{
    using System;
    using Orleans;
    using Orleans.Runtime;

    [GrainInterfaceType("method-interception-custom-name")]
    public interface IMethodInterceptionGrain : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        [Id(14142)]
        Task<string> One();

        [Id(4142)]
        Task<string> Echo(string someArg);
        Task<string> NotIntercepted();
        Task<string> Throw();
        Task<string> IncorrectResultType();
        Task FilterThrows();
    }

    [GrainInterfaceType("custom-outgoing-interception-grain")]
    public interface IOutgoingMethodInterceptionGrain : IGrainWithIntegerKey
    {
        Task<Dictionary<string, object>> EchoViaOtherGrain(IMethodInterceptionGrain otherGrain, string message);
        Task<string> ThrowIfGreaterThanZero(int value);
    }

    public interface IGenericMethodInterceptionGrain<in T> : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        Task<string> GetInputAsString(T input);
    }

    public interface IMethodFromAnotherInterface
    {
        Task<string> SayHello();
    }
    
    public interface ITrickyMethodInterceptionGrain : IGenericMethodInterceptionGrain<string>, IGenericMethodInterceptionGrain<bool>
    {
        Task<int> GetBestNumber();
    }

    public static class GrainCallFilterTestConstants
    {
        public const string Key = "GrainInfo";
    }

    public interface IGrainCallFilterTestGrain : IGrainWithIntegerKey
    {
        Task<string> ThrowIfGreaterThanZero(int value);
        Task<string> GetRequestContext();

        Task<int> SumSet(HashSet<int> numbers);
    }

    public interface IHungryGrain<T> : IGrainWithIntegerKey
    {
        [TestMethodTag("hungry-eat")]
        Task Eat(T food);

        [TestMethodTag("hungry-eatwith")]
        Task EatWith<U>(T food, U condiment);
    }

    public interface IOmnivoreGrain : IGrainWithIntegerKey
    {
        [TestMethodTag("omnivore-eat")]
        Task Eat<T>(T food);
    }

    [Serializable]
    [GenerateSerializer]
    public class Apple { }

    public interface ICaterpillarGrain : IHungryGrain<Apple>, IOmnivoreGrain
    {
        [TestMethodTag("caterpillar-eat")]
        new Task Eat<T>(T food);
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class TestMethodTagAttribute : Attribute
    {
        public TestMethodTagAttribute(string tag) => this.Tag = tag;
        public string Tag { get; }
    }
}

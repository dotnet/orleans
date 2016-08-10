using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace GrainInterfaces
{
    public interface IDataflowGrain : IGrainWithGuidKey
    {
        Task Complete();

        Task Fault();

        Task Completion();
    }

    public interface ITargetGrain<in TInput> : IDataflowGrain
    {
        Task<GrainDataflowMessageStatus> OfferMessage(TInput messageValue, bool consumeToAccept);

        Task SendAsync(TInput t);

        Task SendAsync(TInput t, GrainCancellationToken gct);
    }

    public interface ISourceGrain<TOutput> : IDataflowGrain
    {
        Task LinkTo(ITargetGrain<TOutput> t);

        Task<TOutput> ConsumeMessage();
    }

    public interface IProcessor<in TProcessor>
    {
        Task Initialize(TProcessor processor);
    }

    public interface ITargetProcessor<in TInput>
    {
        void Process(TInput t);
    }

    public interface ITransformProcessor<in TInput, out TOutput>
    {
        TOutput Process(TInput input);
    }

    public interface IPropagatorGrain<in TInput, TOutput> : ITargetGrain<TInput>, ISourceGrain<TOutput>
    {
    }
    
    public interface ITransformGrain<TInput, TOutput> : IPropagatorGrain<TInput, TOutput>, IProcessor<ITransformProcessor<TInput, TOutput>>
    {
    }

    public interface IBufferGrain<T> : IPropagatorGrain<T, T>
    {
        Task<List<T>> ReceiveAll();
    }

    public enum GrainDataflowMessageStatus
    {
        Accepted,
        Declined,
        DecliningPermanently
    }
}

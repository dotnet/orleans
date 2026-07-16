using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.TestingHost.Tests.Grains
{
    public interface IRemoteBlockerGrain : IGrainWithGuidKey
    {
        Task<string> GetSiloIdentity();
        Task BlockUntilReleased();
    }

    public interface ILocalWorkerGrain : IGrainWithGuidKey
    {
        Task CallRemote(IRemoteBlockerGrain remote);
    }

    public interface ILauncherGrain : IGrainWithGuidKey
    {
        Task<string> GetSiloIdentity();
        Task StartBlockingCall(ILocalWorkerGrain worker, IRemoteBlockerGrain remote);
    }

    /// <summary>
    /// A regular grain which blocks until the test releases it, holding the response to a call open so
    /// the caller stays in-flight. State is keyed by the grain's primary key so each test is isolated
    /// from the process-wide statics used by the others.
    /// </summary>
    public class RemoteBlockerGrain : Grain, IRemoteBlockerGrain
    {
        private sealed record BlockState(TaskCompletionSource Release, SemaphoreSlim Entered);

        private static readonly ConcurrentDictionary<Guid, BlockState> States = new();

        private static BlockState GetState(Guid key) =>
            States.GetOrAdd(key, _ => new BlockState(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new SemaphoreSlim(0)));

        public static void Release(Guid key) => GetState(key).Release.TrySetResult();

        public static Task<bool> WaitForEntered(Guid key, TimeSpan timeout) => GetState(key).Entered.WaitAsync(timeout);

        public Task<string> GetSiloIdentity() =>
            Task.FromResult(this.ServiceProvider.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToString());

        public Task BlockUntilReleased()
        {
            var state = GetState(this.GetPrimaryKey());
            state.Entered.Release();
            return state.Release.Task;
        }
    }

    /// <summary>
    /// A stateless worker (placed local to its caller) which makes an outbound call and awaits the
    /// response. When its silo is killed while this call is in-flight, the grain context disposal
    /// awaits deactivation of this worker, which cannot complete until the response arrives - which it
    /// never will. That reproduces the shutdown deadlock unless outstanding callbacks are faulted.
    /// </summary>
    [StatelessWorker(1)]
    public class LocalWorkerGrain : Grain, ILocalWorkerGrain
    {
        public Task CallRemote(IRemoteBlockerGrain remote) => remote.BlockUntilReleased();
    }

    /// <summary>
    /// A regular (identity-placed) grain used to trigger the stateless worker call so the worker is
    /// co-located on this grain's silo, giving the test a deterministic silo to kill.
    /// </summary>
    public class LauncherGrain : Grain, ILauncherGrain
    {
        public Task<string> GetSiloIdentity() =>
            Task.FromResult(this.ServiceProvider.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToString());

        public Task StartBlockingCall(ILocalWorkerGrain worker, IRemoteBlockerGrain remote)
        {
            // Fire-and-forget so this grain does not itself block. The stateless worker is placed on
            // this silo and will block awaiting the remote response.
            _ = worker.CallRemote(remote);
            return Task.CompletedTask;
        }
    }
}

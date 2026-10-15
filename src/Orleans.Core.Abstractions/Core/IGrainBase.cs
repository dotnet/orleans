using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Timers;

namespace Orleans
{
    /// <summary>
    /// Interface for grain implementations
    /// </summary>
    public interface IGrainBase
    {
        /// <summary>
        /// Gets the grain context.
        /// </summary>
        IGrainContext GrainContext { get; }

        /// <summary>
        /// Method overridden by grain implementations to handle activation.
        /// </summary>
        /// <param name="token">The cancellation token used to signify that activation should abort promptly.</param>
        /// <returns>A <see cref="Task"/> which represents the operation.</returns>
        Task OnActivateAsync(CancellationToken token) => Task.CompletedTask;

        /// <summary>
        /// Method overridden by grain implementations to handle deactivation.
        /// </summary>
        /// <param name="reason">The reason for deactivation.</param>
        /// <param name="token">The cancellation token used to signify that deactivation should complete promptly.</param>
        /// <returns>A <see cref="Task"/> which represents the operation.</returns>
        Task OnDeactivateAsync(DeactivationReason reason, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>
    /// Helper methods for <see cref="IGrainBase"/> implementations.
    /// </summary>
    public static class GrainBaseExtensions
    {
        /// <summary>
        /// Deactivate this grain activation after the current grain method call is completed.
        /// This call will mark this activation of the current grain to be deactivated and removed at the end of the current method.
        /// The next call to this grain will result in a different activation to be used, which typical means a new activation will be created automatically by the runtime.
        /// </summary>
        public static void DeactivateOnIdle(this IGrainBase grain) =>
            grain.GrainContext.Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(DeactivateOnIdle)} was called."));

        /// <summary>
        /// Starts an attempt to migrating this instance to another location.
        /// Migration captures the current <see cref="RequestContext"/>, making it available to the activation's placement director so that it can consider it when selecting a new location.
        /// Migration will occur asynchronously, when no requests are executing, and will not occur if the activation's placement director does not select an alternative location.
        /// </summary>
        public static void MigrateOnIdle(this IGrainBase grain) => grain.GrainContext.Migrate(RequestContext.CallContextData?.Value.Values);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync(this IGrainBase grain, Func<CancellationToken, Task> callback, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null) =>
            RunRequestAsync(grain, callback, default(LocalRequestOptions), cancellationToken, methodName);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="options">The options for the request.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync(this IGrainBase grain, Func<CancellationToken, Task> callback, LocalRequestOptions options, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RunRequestCore<Func<CancellationToken, Task>, object?>(
                grain.GrainContext,
                static (callback, cancellationToken) => callback(cancellationToken),
                callback,
                options,
                cancellationToken,
                methodName);
        }

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync<TState>(this IGrainBase grain, Func<TState, CancellationToken, Task> callback, TState state, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null) =>
            RunRequestAsync(grain, callback, state, default(LocalRequestOptions), cancellationToken, methodName);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="options">The options for the request.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync<TState>(this IGrainBase grain, Func<TState, CancellationToken, Task> callback, TState state, LocalRequestOptions options, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RunRequestCore<TState, object?>(grain.GrainContext, callback, state, options, cancellationToken, methodName);
        }

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync(this IGrainBase grain, Action<CancellationToken> callback, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null) =>
            RunRequestAsync(grain, callback, default(LocalRequestOptions), cancellationToken, methodName);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="options">The options for the request.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync(this IGrainBase grain, Action<CancellationToken> callback, LocalRequestOptions options, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RunRequestCore<Action<CancellationToken>, object?>(
                grain.GrainContext,
                static (callback, cancellationToken) =>
                {
                    callback(cancellationToken);
                    return Task.CompletedTask;
                },
                callback,
                options,
                cancellationToken,
                methodName);
        }

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync<TState>(this IGrainBase grain, Action<TState, CancellationToken> callback, TState state, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null) =>
            RunRequestAsync(grain, callback, state, default(LocalRequestOptions), cancellationToken, methodName);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="options">The options for the request.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <returns>A <see cref="Task"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task RunRequestAsync<TState>(this IGrainBase grain, Action<TState, CancellationToken> callback, TState state, LocalRequestOptions options, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RunRequestCore<(Action<TState, CancellationToken> Callback, TState State), object?>(
                grain.GrainContext,
                static (state, cancellationToken) =>
                {
                    state.Callback(state.State, cancellationToken);
                    return Task.CompletedTask;
                },
                (callback, state),
                options,
                cancellationToken,
                methodName);
        }

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TResult">The type of the callback's result.</typeparam>
        /// <returns>A <see cref="Task{TResult}"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task<TResult> RunRequestAsync<TResult>(this IGrainBase grain, Func<CancellationToken, Task<TResult>> callback, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null) =>
            RunRequestAsync(grain, callback, default(LocalRequestOptions), cancellationToken, methodName);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="options">The options for the request.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TResult">The type of the callback's result.</typeparam>
        /// <returns>A <see cref="Task{TResult}"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task<TResult> RunRequestAsync<TResult>(this IGrainBase grain, Func<CancellationToken, Task<TResult>> callback, LocalRequestOptions options, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RunRequestCore<Func<CancellationToken, Task<TResult>>, TResult>(
                grain.GrainContext,
                static (callback, cancellationToken) => callback(cancellationToken),
                callback,
                options,
                cancellationToken,
                methodName);
        }

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <typeparam name="TResult">The type of the callback's result.</typeparam>
        /// <returns>A <see cref="Task{TResult}"/> which completes with the outcome of the callback.</returns>
        /// <remarks><inheritdoc cref="RunRequestAsync{TState, TResult}(IGrainBase, Func{TState, CancellationToken, Task{TResult}}, TState, LocalRequestOptions, CancellationToken, string?)" path="/remarks/node()"/></remarks>
        public static Task<TResult> RunRequestAsync<TState, TResult>(this IGrainBase grain, Func<TState, CancellationToken, Task<TResult>> callback, TState state, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null) =>
            RunRequestAsync(grain, callback, state, default(LocalRequestOptions), cancellationToken, methodName);

        /// <summary>
        /// Runs <paramref name="callback"/> as a request on this grain activation.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The callback to run.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="options">The options for the request.</param>
        /// <param name="cancellationToken">
        /// Cancels the request while it is queued. Once the callback starts, the token is passed to the callback.
        /// </param>
        /// <param name="methodName">
        /// The method name which grain call filters observe for the request. Defaults to the name of the method passed as <paramref name="callback"/>.
        /// A value which is not a simple identifier, such as a lambda expression, is replaced by <c>RunRequestAsync</c>.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <typeparam name="TResult">The type of the callback's result.</typeparam>
        /// <returns>A <see cref="Task{TResult}"/> which completes with the outcome of the callback.</returns>
        /// <remarks>
        /// <para>
        /// The request is scheduled with the activation's other requests. By default, it follows the grain's reentrancy rules:
        /// on a non-reentrant grain, the callback runs exclusively, the same as a grain method call.
        /// The request passes through grain call filters as a method of <c>IGrainRequestInvoker</c> named by <paramref name="methodName"/>.
        /// It starts a new call chain and, by default, keeps the activation alive.
        /// A call filter which fails or sets the result without invoking the request completes the returned task with that outcome.
        /// </para>
        /// <para>
        /// Use this method to run code from a background task, such as a stream or messaging handler, as a grain request.
        /// It can be called from any thread. On a non-reentrant grain, awaiting the returned task from within a request on the same activation deadlocks,
        /// as does awaiting a call to the same activation from within the callback.
        /// </para>
        /// <para>
        /// If the activation deactivates before the request starts, the returned task faults with
        /// <see cref="OrleansMessageRejectionException"/>.
        /// If <see cref="LocalRequestOptions.EnforceQueueLimit"/> is set and the activation's queue exceeds the configured maximum number of enqueued requests,
        /// the returned task faults with <see cref="LimitExceededException"/>.
        /// To queue a custom <see cref="Orleans.Serialization.Invocation.IInvokable"/>, use <see cref="IGrainContext.ReceiveLocalRequest"/>.
        /// </para>
        /// </remarks>
        public static Task<TResult> RunRequestAsync<TState, TResult>(this IGrainBase grain, Func<TState, CancellationToken, Task<TResult>> callback, TState state, LocalRequestOptions options, CancellationToken cancellationToken = default, [CallerArgumentExpression(nameof(callback))] string? methodName = null)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RunRequestCore<TState, TResult>(grain.GrainContext, callback, state, options, cancellationToken, methodName);
        }

        private static Task<TResult> RunRequestCore<TState, TResult>(
            IGrainContext grainContext,
            Func<TState, CancellationToken, Task> callback,
            TState state,
            LocalRequestOptions options,
            CancellationToken cancellationToken,
            string? methodName)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<TResult>(cancellationToken);
            }

            // The activation owns the request once it is queued and disposes it after invocation.
#pragma warning disable CA2000 // Dispose objects before losing scope
            var request = new GrainRequest<TState, TResult>(callback, state, IsMemberName(methodName) ? methodName : null, cancellationToken);
#pragma warning restore CA2000 // Dispose objects before losing scope
            grainContext.ReceiveLocalRequest(request, request, options);
            return request.Task;
        }

        // A simple method group, such as ProcessAsync, names the request. Other expressions, such as lambdas, use the default name.
        private static bool IsMemberName(string? name) => name is { Length: > 0 } && !name.AsSpan().ContainsAnyExcept(MemberNameChars);

        private static readonly SearchValues<char> MemberNameChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="options">
        /// The options for creating the timer.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <returns>
        /// The <see cref="IGrainTimer"/> instance which represents the timer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
        /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
        /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
        /// When a grain is deactivated, all active timers are discarded.
        /// </para>
        /// <para>
        /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
        /// That is to say, a timer callback will never be concurrently executed with itself.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
        /// to interleave with with other grain method calls and other timers.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
        /// reentrancy setting of the grain, just like a typical grain method call.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
        /// Disposing a timer prevents any further timer ticks from being scheduled.
        /// </para>
        /// <para>
        /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
        /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
        /// Subsequent ticks will be scheduled after the updated period elapses.
        /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
        /// </para>
        /// <para>
        /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (grain is Grain grainClass)
            {
                ArgumentNullException.ThrowIfNull(callback);

                grainClass.EnsureRuntime();
                return grainClass.Runtime.TimerRegistry.RegisterGrainTimer(grainClass.GrainContext, callback, state, options);
            }

            return grain.GrainContext.ActivationServices.GetRequiredService<ITimerRegistry>().RegisterGrainTimer(grain.GrainContext, callback, state, options);
        }

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="options">
        /// The options for creating the timer.
        /// </param>
        /// <returns>
        /// The <see cref="IGrainTimer"/> instance which represents the timer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
        /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
        /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
        /// When a grain is deactivated, all active timers are discarded.
        /// </para>
        /// <para>
        /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
        /// That is to say, a timer callback will never be concurrently executed with itself.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
        /// to interleave with with other grain method calls and other timers.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
        /// reentrancy setting of the grain, just like a typical grain method call.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
        /// Disposing a timer prevents any further timer ticks from being scheduled.
        /// </para>
        /// <para>
        /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
        /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
        /// Subsequent ticks will be scheduled after the updated period elapses.
        /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
        /// </para>
        /// <para>
        /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<CancellationToken, Task> callback, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RegisterGrainTimer(grain, static (callback, cancellationToken) => callback(cancellationToken), callback, options);
        }

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="options">The options for creating the timer.</param>
        /// <returns>The <see cref="IGrainTimer"/> instance which represents the timer.</returns>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<Task> callback, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RegisterGrainTimer(grain, static (callback, cancellationToken) => callback(), callback, options);
        }

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, GrainTimerCreationOptions)"/>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="options">The options for creating the timer.</param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, Task> callback, TState state, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RegisterGrainTimer(grain, static (state, _) => state.Callback(state.State), (Callback: callback, State: state), options);
        }

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="dueTime">
        /// A <see cref="TimeSpan"/> representing the amount of time to delay before invoking the callback method specified when the <see cref="IGrainTimer"/> was constructed.
        /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to start the timer immediately.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of the callback method specified when the <see cref="IGrainTimer"/> was constructed.
        /// Specify <see cref="Timeout.InfiniteTimeSpan "/> to disable periodic signaling.
        /// </param>
        /// <returns>
        /// The <see cref="IGrainTimer"/> instance which represents the timer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
        /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
        /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
        /// When a grain is deactivated, all active timers are discarded.
        /// </para>
        /// <para>
        /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
        /// That is to say, a timer callback will never be concurrently executed with itself.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
        /// to interleave with with other grain method calls and other timers.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
        /// reentrancy setting of the grain, just like a typical grain method call.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
        /// Disposing a timer prevents any further timer ticks from being scheduled.
        /// </para>
        /// <para>
        /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
        /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
        /// Subsequent ticks will be scheduled after the updated period elapses.
        /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
        /// </para>
        /// <para>
        /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<Task> callback, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, new() { DueTime = dueTime, Period = period });

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, TimeSpan, TimeSpan)"/>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, new() { DueTime = dueTime, Period = period });

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, TimeSpan, TimeSpan)"/>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="dueTime">The amount of time to delay before invoking the callback.</param>
        /// <param name="period">The time interval between callback invocations.</param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, state, new() { DueTime = dueTime, Period = period });

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, TimeSpan, TimeSpan)"/>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="dueTime">The amount of time to delay before invoking the callback.</param>
        /// <param name="period">The time interval between callback invocations.</param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, CancellationToken, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, state, new() { DueTime = dueTime, Period = period });
    }

    /// <summary>
    /// An informational reason code for deactivation.
    /// </summary>
    [GenerateSerializer]
    public enum DeactivationReasonCode : byte
    {
        /// <summary>
        /// No reason provided.
        /// </summary>
        None,

        /// <summary>
        /// The process is currently shutting down.
        /// </summary>
        ShuttingDown,

        /// <summary>
        /// Activation of the grain failed.
        /// </summary>
        ActivationFailed,

        /// <summary>
        /// This activation is affected by an internal failure in the distributed grain directory.
        /// </summary>
        /// <remarks>
        /// This could be caused by the failure of a process hosting this activation's grain directory partition, for example.
        /// </remarks>
        DirectoryFailure,

        /// <summary>
        /// This activation is idle.
        /// </summary>
        ActivationIdle,

        /// <summary>
        /// This activation is unresponsive to commands or requests.
        /// </summary>
        ActivationUnresponsive,

        /// <summary>
        /// Another instance of this grain has been activated.
        /// </summary>
        DuplicateActivation,

        /// <summary>
        /// This activation received a request which cannot be handled by the locally running process.
        /// </summary>
        IncompatibleRequest,

        /// <summary>
        /// An application error occurred.
        /// </summary>
        ApplicationError,

        /// <summary>
        /// The application requested to deactivate this activation.
        /// </summary>
        ApplicationRequested,

        /// <summary>
        /// This activation is migrating to a new location.
        /// </summary>
        Migrating,

        /// <summary>
        /// The runtime requested to deactivate this activation.
        /// </summary>
        RuntimeRequested,

        /// <summary>
        /// Runtime detected that app is running on low memory, and forcefully decided to deactivate.
        /// </summary>
        HighMemoryPressure,
    }

}

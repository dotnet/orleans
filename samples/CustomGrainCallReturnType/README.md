# Custom grain-call return type

This sample defines `GrainCall<T>`, a task-backed awaitable used directly in a grain interface. Orleans generates a request type derived from `GrainCallRequest<T>` for each grain method.

The generated proxy:

1. Creates the request and copies the method arguments into it.
2. Calls `GrainCallRequest<T>.InitializeRequest`.
3. Submits the request through `IGrainReferenceRuntime`.
4. Returns `GrainCall<T>` to the application.

On the target activation, the generated request calls the grain implementation. `GrainCallRequest<T>.Invoke` awaits the returned wrapper and converts its value or exception into an Orleans response.

`GrainCall<T>` starts one invocation immediately, supports multiple awaiters through its backing `Task<T>`, and propagates the remote terminal result. Cancellation is supplied as a grain method `CancellationToken` argument when a contract needs cooperative cancellation.

## Run the sample

The sample targets .NET 10 and uses localhost clustering, so it requires no external services.

```powershell
dotnet run --project CustomGrainCallReturnType.csproj
```

The client prints a successful result and then observes an exception returned by the grain.

For the registration rules and compatibility guidance, see [Customize Orleans serialization code generation](../../docs/site/src/content/docs/host/configuration-guide/serialization-code-generation-customization.md). For the generated proxy, request, dispatch, and response path, see [Serialization and code generation internals](../../docs/site/src/content/docs/implementation/serialization.md).

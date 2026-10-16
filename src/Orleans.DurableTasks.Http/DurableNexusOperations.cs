using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Orleans.DurableTasks.Http;

/// <summary>Serves durable operations with the Nexus protocol: start and cancel, addressed by operation tokens.</summary>
/// <remarks>See <see href="https://github.com/nexus-rpc/api/blob/main/SPEC.md">the Nexus HTTP specification</see>.</remarks>
internal sealed class DurableNexusOperations(
    DurableOperationService operations,
    DurableCallbackRegistrar callbacks,
    INexusOperationTokenFormat tokens,
    IOptions<DurableHttpOptions> options,
    IOptions<HttpJsonOptions> jsonOptions)
{
    private readonly DurableHttpOptions _options = options.Value;
    private readonly JsonSerializerOptions _json = jsonOptions.Value.SerializerOptions;

    /// <summary>
    /// Starts <paramref name="work"/> as the operation which the request's <c>Nexus-Request-Id</c> names, and returns its
    /// result when it is terminal or its token while it runs.
    /// </summary>
    public async Task<IResult> StartAsync(HttpContext context, DurableTask work)
    {
        var requestId = context.Request.Headers[NexusProtocol.RequestId];
        var operationId = requestId.Count == 0 ? Guid.NewGuid().ToString("N") : requestId.ToString();
        if (requestId.Count > 1 || !DurableHttpRequestHeaders.IsValidOperationId(operationId))
        {
            return NexusProtocol.HandlerError(
                StatusCodes.Status400BadRequest,
                "BAD_REQUEST",
                $"A Nexus-Request-Id has 1 to {DurableHttpRequestHeaders.MaxIdempotencyKeyLength} printable ASCII characters other than '/'.");
        }


        if (!callbacks.TryRead(
            context,
            context.Request.Query[NexusProtocol.CallbackQuery].ToString(),
            NexusProtocol.CallbackHeaderPrefix,
            out var callback,
            out var callbackError))
        {
            return NexusProtocol.HandlerError(StatusCodes.Status400BadRequest, "BAD_REQUEST", callbackError!);
        }

        DurableTaskResponse response;
        var key = NexusServiceMetadata.Get(context).GetKey(_options.MapOperationId(context, operationId));
        try
        {
            response = await operations.StartAsync(key, work, TimeSpan.Zero, context.RequestAborted);
        }
        catch (DurableTaskConflictException)
        {
            return NexusProtocol.HandlerError(
                StatusCodes.Status409Conflict,
                "CONFLICT",
                "The Nexus-Request-Id identifies an operation which another request started.");
        }
        catch (Exception exception) when (exception is DurableTaskRejectedException or DurableTaskDeliveryException)
        {
            return NexusProtocol.HandlerError(
                StatusCodes.Status503ServiceUnavailable,
                "UNAVAILABLE",
                "The grain which runs the operation didn't accept it.");
        }

        switch (response.Status)
        {
            case DurableTaskStatus.CompletedSuccessfully:
                context.Response.Headers[NexusProtocol.OperationState] = NexusProtocol.Succeeded;
                return response.ResultType is { } resultType ? Results.Json(response.Result, _json.GetTypeInfo(resultType)) : TypedResults.Ok();
            case DurableTaskStatus.Failed or DurableTaskStatus.Canceled:
                var error = _options.CreateError(response.Exception!);
                return TypedResults.Json(
                    new NexusFailure(
                        error.Message,
                        new() { ["type"] = NexusProtocol.OperationErrorType },
                        new() { ["state"] = response.Status == DurableTaskStatus.Failed ? NexusProtocol.Failed : NexusProtocol.Canceled, ["code"] = error.Code }),
                    NexusJsonContext.Default.NexusFailure,
                    statusCode: StatusCodes.Status424FailedDependency);
            default:
                var token = tokens.Format(operationId);
                if (callback is not null)
                {
                    await callbacks.RegisterAsync(callback, DurableCallbackProtocol.Nexus, operations.GetHostId(key), token, operationLocation: null, context.RequestAborted);
                }

                return TypedResults.Json(
                    new NexusOperationInfo(token, NexusProtocol.Running),
                    NexusJsonContext.Default.NexusOperationInfo,
                    statusCode: StatusCodes.Status201Created);
        }
    }

    /// <summary>
    /// Durably requests cancellation of the operation which the request's token names, and returns <c>202 Accepted</c>,
    /// including for a terminal operation, which is unchanged.
    /// </summary>
    public async Task<IResult> CancelAsync(HttpContext context)
    {
        var header = context.Request.Headers[NexusProtocol.OperationToken];
        var token = header.Count > 0 ? header.ToString() : context.Request.Query[NexusProtocol.TokenQuery].ToString();
        if (!tokens.TryParse(token, out var operationId))
        {
            return NotFound();
        }

        try
        {
            var key = NexusServiceMetadata.Get(context).GetKey(_options.MapOperationId(context, operationId));
            await operations.CancelAsync(key, reason: null, context.RequestAborted);
        }
        catch (DurableTaskNotFoundException)
        {
            return NotFound();
        }

        return TypedResults.Accepted((string?)null);
    }

    private static IResult NotFound() =>
        NexusProtocol.HandlerError(StatusCodes.Status404NotFound, "NOT_FOUND", "The operation doesn't exist, or its retention has ended.");
}

namespace DurableWorkflows;

public sealed record EffectSubmission(EffectIdentity Identity, EffectRequest Request);
public sealed record EffectCancellation(EffectIdentity Identity, string Reason);
public sealed record EffectResolution(EffectIdentity Identity, long ExpectedRevision, ProviderOutcome Outcome, string Evidence);
public sealed record EffectFollowUp(EffectIdentity Original, EffectIdentity FollowUp, string Authorization, EffectRequest? Request = null);

public static class ExternalEffectEndpoints
{
    public static void MapExternalEffectEndpoints(this WebApplication app)
    {
        var effects = app.MapGroup("/external-effects/{owner}");
        effects.MapPost("/", (string owner, EffectSubmission submission, IClusterClient client) =>
            ExecuteAsync(() => client.GetGrain<IExternalEffectGrain>(owner).SubmitAsync(submission.Identity, submission.Request)));
        effects.MapPost("/inspect", (string owner, EffectIdentity identity, IClusterClient client) =>
            ExecuteAsync(() => client.GetGrain<IExternalEffectGrain>(owner).GetAsync(identity)));
        effects.MapPost("/run", async (string owner, IClusterClient client) =>
        {
            await client.GetGrain<IExternalEffectGrain>(owner).RunDueAsync();
            return Results.Accepted();
        });
        effects.MapPost("/cancel", (string owner, EffectCancellation request, IClusterClient client) =>
            ExecuteAsync(() => client.GetGrain<IExternalEffectGrain>(owner).CancelAsync(request.Identity, request.Reason)));
        effects.MapPost("/resolve", (string owner, EffectResolution request, IClusterClient client) =>
            ExecuteAsync(() => client.GetGrain<IExternalEffectGrain>(owner)
                .ResolveAsync(request.Identity, request.ExpectedRevision, request.Outcome, request.Evidence)));
        effects.MapPost("/follow-up", (string owner, EffectFollowUp request, IClusterClient client) =>
            ExecuteAsync(() => client.GetGrain<IExternalEffectGrain>(owner)
                .AuthorizeFollowUpAsync(request.Original, request.FollowUp, request.Authorization, request.Request)));

        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/external-effect-providers/{account}/behavior/{behavior}",
                async (string account, RecipeProviderBehavior behavior, IClusterClient client) =>
                {
                    await client.GetGrain<IRecipeEffectProviderGrain>(account).ConfigureAsync(behavior);
                    return Results.NoContent();
                });
        }
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { exception.Message });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }
}

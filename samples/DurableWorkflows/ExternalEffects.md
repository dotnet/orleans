# Payment and email effects

`ExternalEffectGrain` owns each effect's lifecycle in its journal. The provider owns its payment authorization or
email acceptance outcome. Reconciliation records the application's evidence and decision. `RecipeEffectProviderGrain`
is an independently journaled demonstration service, so these recipes run locally with the sample's Aspire host.
Production gateways implement the same dispatch and lookup contracts using the payment or email provider's API.

## Run a payment and its outgoing receipt

Start the sample as described in [README.md](README.md). In PowerShell, set `$service` to the service URL from the Aspire
dashboard. Keep the identity and original request with the order; repeat submissions reuse these values.

```powershell
$service = 'https://localhost:YOUR_SERVICE_PORT'
$order = 'payment-' + [guid]::NewGuid().ToString('N')
$account = 'provider-' + $order
$identity = @{
    executionId = 'checkout'; incarnation = [guid]::NewGuid().ToString()
    operationId = 'authorize'; occurrence = 0
}
$request = @{
    kind = 0; providerAccount = $account; destination = 'customer-account'
    content = $order; amount = 12.50; currency = 'USD'
    onSuccess = @{
        kind = 1; providerAccount = $account; destination = 'customer@example.invalid'
        content = "Receipt for $order"
    }
}
function Send-EffectCommand($path, $body) {
    Invoke-RestMethod "$service/$path" -Method Post -ContentType 'application/json' `
        -Body (ConvertTo-Json -InputObject $body -Depth 10)
}
$accepted = Send-EffectCommand "external-effects/$order/" @{ identity = $identity; request = $request }
# Durable Jobs dispatches committed pending effects. /run also advances work which is already due.
Invoke-RestMethod "$service/external-effects/$order/run" -Method Post
$payment = Send-EffectCommand "external-effects/$order/inspect" $identity
$payment
$receiptIdentity = $identity.Clone()
$receiptIdentity.operationId = 'authorize/on-success'
$receipt = Send-EffectCommand "external-effects/$order/inspect" $receiptIdentity
$receipt
```

`kind` is `0` for payment authorization, `1` for idempotent email, `2` for email requiring reconciliation after
interruption, and `3` for payment refund. A successful authorization records
`businessState: "authorized;ready-to-fulfill"` and the next email
intent together. Its `nextEffectKey` identifies that intent. The durable outbox stages an outcome message to
`EffectOrderGrain` in that same write; the receiving inbox commits its order projection with message completion.
The owner's effect record remains the lifecycle authority. Monitor Messaging dead letters and reconcile a delivery
which exhausts its retry window against that record.

`SubmitAsync` acknowledges committed intent. `ExternalEffectWorkflowGrain.RunAsync` demonstrates durable-task
composition: it creates an identity from `DurableTask.Current`, submits the intent in a named step, and uses named
reads and durable delays until a recorded terminal outcome exists.

```csharp
var workflow = client.GetGrain<IExternalEffectWorkflowGrain>(orderId);
var handle = await workflow.RunAsync(paymentRequest).WithId("checkout").ScheduleAsync();
var result = await handle.WaitAsync(cancellationToken);
```

The workflow's retained result is a snapshot of the owner's terminal outcome. Stopping a client wait or canceling
the polling workflow stops that observation. Cancel the effect through its owner's `CancelAsync` or `/cancel`
operation when the business operation should stop dispatching.

## Exercise a lost response and a decline

The Development environment exposes demonstration provider controls. Configure a fresh provider account before
submitting an effect. Provider behavior values are `Apply`, `Reject`, `ProveNoEffect`, `LoseResponseAfterApply`,
`LookupUnavailable`, and `UnavailableBeforeApply`.

```powershell
$order = 'lost-' + [guid]::NewGuid().ToString('N')
$account = 'provider-' + $order
$identity.incarnation = [guid]::NewGuid().ToString()
$request.providerAccount = $account
$request.content = $order
Invoke-RestMethod "$service/external-effect-providers/$account/behavior/LoseResponseAfterApply" -Method Post
$accepted = Send-EffectCommand "external-effects/$order/" @{ identity = $identity; request = $request }
Invoke-RestMethod "$service/external-effects/$order/run" -Method Post
Start-Sleep -Seconds 6
Invoke-RestMethod "$service/external-effects/$order/run" -Method Post
Send-EffectCommand "external-effects/$order/inspect" $identity

$order = 'decline-' + [guid]::NewGuid().ToString('N')
$account = 'provider-' + $order
$identity.incarnation = [guid]::NewGuid().ToString()
$request.providerAccount = $account
$request.content = $order
Invoke-RestMethod "$service/external-effect-providers/$account/behavior/Reject" -Method Post
Send-EffectCommand "external-effects/$order/" @{ identity = $identity; request = $request }
Invoke-RestMethod "$service/external-effects/$order/run" -Method Post
Send-EffectCommand "external-effects/$order/inspect" $identity
```

The lost response leaves an uncertain attempt. A safe retry uses the original provider key and the provider returns
its recorded authorization. A decline completes as `Rejected` with `businessState: "business-rejected"` and creates
the rejection outcome message. The success email intent is created only for an applied authorization.

## Reconcile unkeyed email and authorize a deliberate resend

The unkeyed email gateway demonstrates a provider which accepts email and offers operator-assisted investigation.
A lost response leaves the original request, key, attempt evidence, resolution owner, reason, and next check time
in the owner's journal. Scheduled queries preserve uncertainty when they fail or return inconclusive evidence.

```powershell
$order = 'email-' + [guid]::NewGuid().ToString('N')
$account = 'provider-' + $order
$emailIdentity = @{
    executionId = 'notification'; incarnation = [guid]::NewGuid().ToString()
    operationId = 'receipt'; occurrence = 0
}
$emailRequest = @{
    kind = 2; providerAccount = $account; destination = 'customer@example.invalid'; content = 'Your receipt'
}
Invoke-RestMethod "$service/external-effect-providers/$account/behavior/LoseResponseAfterApply" -Method Post
Send-EffectCommand "external-effects/$order/" @{ identity = $emailIdentity; request = $emailRequest }
Invoke-RestMethod "$service/external-effects/$order/run" -Method Post
Start-Sleep -Seconds 6
Invoke-RestMethod "$service/external-effects/$order/run" -Method Post
$pending = Send-EffectCommand "external-effects/$order/inspect" $emailIdentity
$pending | Select-Object status, providerKey, attempts, evidence, resolutionOwner, nextAction, nextActionAt, revision

# After obtaining the provider receipt, the authorized operator records the decision.
# ProviderOutcome: Applied=0, Rejected=1, ProvenNoEffect=2, Unknown=3.
Send-EffectCommand "external-effects/$order/resolve" @{
    identity = $emailIdentity; expectedRevision = $pending.revision
    outcome = 0; evidence = 'Support case 42: provider receipt verified'
}

# A customer-requested second copy is a new operation with its own key and explicit authorization.
$resend = $emailIdentity.Clone()
$resend.operationId = 'customer-requested-copy'
Send-EffectCommand "external-effects/$order/follow-up" @{
    original = $emailIdentity; followUp = $resend
    authorization = 'Support case 43: customer authorized a second copy'
}
```

Authorize a follow-up after the active attempt has settled. The original points to the new effect; the new effect
retains the original key and the authorization. An unresolved original remains retained and continues reconciliation
with future dispatch disabled. A reconciliation decision uses the displayed revision, so a newer decision wins over
late provider responses. A stale revision returns `409 Conflict`; inspect the current record before deciding again.
Protect cancellation, resolution, policy changes, follow-up authorization, and provider diagnostics with application
authentication and authorization in a deployed service.

## Protocol and policy

An identity consists of the owning grain ID, durable execution ID, root-run incarnation, stable operation name and
occurrence. `EffectIdentity.Current("authorize", occurrence)` captures the task's persisted run identity. Loop iterations
use distinct occurrence values. SHA-256 over an unambiguous JSON array produces the provider key. Attempt GUIDs and
durable child-task IDs remain separate. A repeated identity returns its recorded request/result; conflicting request
content fails explicitly. A newly authorized business operation gets a new identity. The `/on-success` suffix is
reserved for the original request's derived outgoing intent.

Each effect snapshots its policy: version, execution mode, maximum consumed attempts, backoff, attempt timeout,
overall deadline, idempotency safety deadline, and reconciliation interval. The service binds defaults from the
`ExternalEffects` configuration section. New configuration governs new submissions; recovery uses the stored
policy, consumed attempts, and next action time. `ChangePolicyAsync` accepts a validated, explicitly authorized
policy change for an effect with no attempted dispatch. Unsupported stored policies remain blocked with an
operator action and a scheduled check. A software upgrade restores support for attempted effects with such a policy.
`AddExternalEffectRecipes()` registers the owner's keyed `TimeProvider` (`external-effects`) and keeps failed
infrastructure wake-ups eligible for retry. The persisted effect policy independently limits provider dispatches.

Retry-safe payment and idempotent email gateways promise repeatability for one key through the stored safety
deadline. Set `ProviderKeyLifetime` conservatively from the provider's guaranteed retention, allowing for clock
uncertainty and the maximum request-processing delay. The owner admits an attempt only when its timeout fits
strictly before both its overall and safety deadlines. An expired deadline or exhausted retry budget switches an
uncertain effect to lookup/operator reconciliation. A sequence of proven no-effect attempts can complete as a
confirmed failure. A later attempt's no-effect failure preserves any uncertainty from earlier attempts.

For reconciliation-on-interruption email, the owner persists an attempt marker before dispatch. A crash between
that marker and dispatch is uncertain too. Provider rejection is a business outcome; a documented no-effect
failure applies only to the attempt it describes. Timeout, generic exceptions, lost responses and cancellation
all preserve possible application. A provider adapter returns `Applied` or `Rejected` only for an authoritative
logical-key outcome, and `ProvenNoEffect` only when its contract establishes that evidence.

Every actionable pending write first acknowledges a Durable Jobs wake-up. A failed intent write leaves provider
dispatch untouched. A committed marker and remote success survive a failed completion write through recovery and
the original key. Completion, related business state, the next effect intent, and the outcome outbox message share
the owner's journal write. The next wake-up is established before querying reconciliation evidence too.

Attempt cancellation is cooperative and local. Durable effect cancellation records the request, stops future
dispatch, and preserves any potentially applied outcome for lookup or an operator. A confirmed successful payment
can require a refund, modeled as a separately authorized compensation effect. All records remain retained in this
recipe. A production retention operation preserves unresolved records and keeps completed identities for at least
the application replay window and provider idempotency window.

For example, `AuthorizeFollowUpAsync(authorizationIdentity, refundIdentity, approvalReference,
paymentRequest with { Kind = EffectKind.PaymentRefund, OnSuccess = null })` records the refund's authorization
and relationship to the original payment. The original authorization and its receipt intent remain intact.

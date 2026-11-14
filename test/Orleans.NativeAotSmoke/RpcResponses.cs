using System;
using Orleans.NativeAotSmoke;

RpcResponseContracts.PrimitiveResponses();
RpcResponseContracts.ResponseCodecPublication();
await RpcResponseContracts.GeneratedInvokablesProduceCopiedResponses();
RpcResponseContracts.CanonicalValueAndArrayServices();
RpcResponseContracts.ConstructTupleArgumentProxyBeforeInvocation();
RpcResponseContracts.ReferenceResponsePreservesCycles();
RpcResponseContracts.NullResponsePayload();
RpcResponseContracts.CompletedAndExceptionResponses();
RpcResponseContracts.CompletedResponseRoundTrip();
RpcResponseContracts.ExceptionResponsesRoundTrip();
RpcResponseContracts.ExceptionResponseDataRoundTrip();
RpcResponseContracts.UnregisteredExceptionResponseRoundTrip();
RpcResponseContracts.RawResponses();
RpcResponseContracts.MissingNativeResponseRegistration();
RpcConstructionContracts.DefaultGraphsRespectConstructorDependencies();
Console.WriteLine("Native Response dispatch passed: DeepCopier<Response>, bool, int, reference cycles, null, completion identity, exception transport and data, raw message encoding, and canonical constructor activation after default graph declination.");

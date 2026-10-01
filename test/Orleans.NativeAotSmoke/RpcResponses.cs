using System;
using Orleans.NativeAotSmoke;

RpcResponseContracts.PrimitiveResponses();
RpcResponseContracts.ReferenceResponsePreservesCycles();
RpcResponseContracts.NullResponsePayload();
RpcResponseContracts.CompletedAndExceptionResponses();
RpcResponseContracts.CompletedResponseRoundTrip();
RpcResponseContracts.ExceptionTransportRequiresDeclaredGraph();
RpcResponseContracts.RawResponses();
RpcResponseContracts.MissingNativeResponseRegistration();
Console.WriteLine("Native Response dispatch passed: DeepCopier<Response>, bool, int, reference cycles, null, completion/exception identity, and raw message encoding.");

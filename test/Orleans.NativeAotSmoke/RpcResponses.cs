using System;
using Orleans.NativeAotSmoke;

RpcResponseContracts.PrimitiveResponses();
RpcResponseContracts.GeneratedFactoryPublication();
await RpcResponseContracts.GeneratedInvokablesWriteCopiedResponses();
RpcResponseContracts.CanonicalValueAndArrayServices();
RpcResponseContracts.ConstructTupleArgumentProxyBeforeInvocation();
RpcResponseContracts.ReferenceResponsePreservesCycles();
RpcResponseContracts.NullResponsePayload();
RpcResponseContracts.CompletedAndExceptionResponses();
RpcResponseContracts.CompletedResponseRoundTrip();
RpcResponseContracts.ExceptionTransportRequiresDeclaredGraph();
RpcResponseContracts.RawResponses();
RpcResponseContracts.MissingNativeResponseRegistration();
RpcConstructionContracts.DefaultGraphsRespectConstructorDependencies();
Console.WriteLine("Native Response dispatch passed: DeepCopier<Response>, bool, int, reference cycles, null, completion/exception identity, raw message encoding, and canonical constructor activation after default graph declination.");

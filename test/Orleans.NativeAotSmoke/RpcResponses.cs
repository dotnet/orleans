using System;
using Orleans.NativeAotSmoke;

RpcResponseContracts.PrimitiveResponses();
RpcResponseContracts.ReferenceResponsePreservesCycles();
RpcResponseContracts.NullResponsePayload();
RpcResponseContracts.RawResponses();
RpcResponseContracts.MissingNativeResponseRegistration();
Console.WriteLine("Native RPC response factories passed: bool, int, reference payloads, cycles, null, and raw message encoding.");

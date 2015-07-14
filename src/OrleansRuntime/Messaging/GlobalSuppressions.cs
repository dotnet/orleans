/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

// This file is used by Code Analysis to maintain SuppressMessage 
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given 
// a specific target and scoped to a namespace, type, member, etc.
//
// To add a suppression to this file, right-click the message in the 
// Error List, point to "Suppress Message(s)", and click 
// "In Project Suppression File".
// You do not need to add suppressions to this file manually.

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1014:MarkAssembliesWithClsCompliant")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#ReceiveCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#Stop()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.OutgoingMessageSender.#SendMessage(Orleans.Message)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.SocketManager.#CloseSocket(System.Net.Sockets.Socket)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.SocketManager.#SendingSocketCreator(System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Scope = "member", Target = "Orleans.Messaging.SocketManager.#ReturnSendingSocket(System.Net.Sockets.Socket)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#AcceptCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.SocketManager.#AcceptingSocketCreator()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.SocketManager.#ReceiveCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Info(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#.ctor(Orleans.Messaging.MessageCenter,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#AcceptCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#ReceiveCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#Run()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#Stop()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Info(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(Orleans.IRouter,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Info(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Info(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(System.String,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Error(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#CreateRouter(System.String)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.OutgoingMessageSender.#Run()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Info(System.String)", Scope = "member", Target = "Orleans.Messaging.OutgoingMessageSender.#SendMessage(Orleans.Message)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.OutgoingMessageSender.#SendMessage(Orleans.Message)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1020:AvoidNamespacesWithFewTypes", Scope = "namespace", Target = "Orleans.Messaging")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.InboundMessageQueue.#PostMessage(Orleans.Message)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Error(System.String,System.Exception)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#Run()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.OutboundMessageQueue.#SendMessage(Orleans.Message)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Info(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#AcceptCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "EndAccept", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#AcceptCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#HandleMessage(Orleans.Messaging.IncomingMessageAcceptor,System.Byte[])")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Error(System.String,System.Exception)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#RestartAcceptingSocket()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor+ReceiveCallbackContext.#ProcessReceivedBuffer(System.Int32,System.Action`2<Orleans.Messaging.IncomingMessageAcceptor,System.Byte[]>)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(Orleans.IRouter,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(System.String,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose3(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#AcceptCallback(System.IAsyncResult)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose2(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#HandleMessage(Orleans.Messaging.IncomingMessageAcceptor,System.Byte[])")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose3(System.String)", Scope = "member", Target = "Orleans.Messaging.IncomingMessageAcceptor.#Run()")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose3(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(Orleans.IRouter,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose3(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose3(System.String)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#.ctor(System.String,System.Net.IPEndPoint)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Error(System.String,System.Exception)", Scope = "member", Target = "Orleans.Messaging.MessageCenter.#CreateRouter(System.String)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "Orleans.Logger.Verbose2(System.String)", Scope = "member", Target = "Orleans.Messaging.OutboundMessageQueue.#SendMessage(Orleans.Message)")]

namespace Orleans.Runtime.Messaging
{
}
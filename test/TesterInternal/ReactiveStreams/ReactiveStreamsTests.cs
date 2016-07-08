using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.ReactiveStreams
{
    public class ReactiveStreamsTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional")]
        public async Task AdHocStreamTest()
        {
            var grain = GrainFactory.GetGrain<IReactiveGrain<int>>(Guid.NewGuid());
            var expected = new[] {1, 2, 3, 4, 5, 6};
            var stream = grain.GetStream(expected);
            var observer = new BufferedObserver<int>();
            var disposable = await stream.Subscribe(observer);
            await disposable.Dispose();
            Assert.Equal(expected, observer.Buffer);
        }

        [Fact, TestCategory("Functional")]
        public async Task AdHocStreamTestWithStrings()
        {
            var grain = GrainFactory.GetGrain<IReactiveGrain<string>>(Guid.NewGuid());
            var expected = new[] {"always", "leave", "a", "note"};
            var stream = grain.GetStream(expected);
            var observer = new BufferedObserver<string>();
            var disposable = await stream.Subscribe(observer);
            await disposable.Dispose();
            Assert.Equal(expected, observer.Buffer);
        }

        [Fact, TestCategory("Functional")]
        public async Task ClientToGrainToGrainAdhocStreamingTest()
        {
            var roomGrain = GrainFactory.GetGrain<IChatRoomGrain>("#orleans");
            await roomGrain.SendMessage("NO ONE WILL RECEIVE THIS!");
            var reactiveGrain = GrainFactory.GetGrain<IReactiveGrain<string>>(Guid.NewGuid());

            // Join the room via a proxy grain.
            var messagesFromProxyGrain = new BufferedObserver<string>();
            var proxySubscription = await reactiveGrain.JoinChatRoom("#orleans").Subscribe(messagesFromProxyGrain);

            // Join the room directly.
            Assert.Equal(1, await roomGrain.GetCurrentUserCount());
            var messagesFromRoom = new BufferedObserver<string>();
            var subscription = await roomGrain.JoinRoom().Subscribe(messagesFromRoom);
            Assert.Equal(2, await roomGrain.GetCurrentUserCount());

            // Send a couple of messages to the room.
            var messages = new[] {"These things are fun", "Issue #940 needs some attention..."};
            await roomGrain.SendMessage(messages[0]);
            await roomGrain.SendMessage(messages[1]);

            // Leave the room by disposing one of the subscriptions.
            await proxySubscription.Dispose();
            Assert.Equal(1, await roomGrain.GetCurrentUserCount());

            // Send another message.
            await roomGrain.SendMessage(messages[1].ToUpperInvariant()).ContinueWith(_ => _);

            // Check that the observers received the right messages.
            Assert.Equal(messages, messagesFromProxyGrain.Buffer);
            await subscription.Dispose();
            Assert.Equal(
                new[] {messages[0], messages[1], messages[1].ToUpperInvariant()},
                messagesFromRoom.Buffer);
            Assert.Equal(0, await roomGrain.GetCurrentUserCount());
            await roomGrain.SendMessage("NO ONE WILL RECEIVE THIS!");
        }

        [Fact, TestCategory("Functional")]
        public async Task GrainToGrainAdhocStreamingTest()
        {
            var jeff = GrainFactory.GetGrain<IChatUserGrain>("@JeffBezos");
            var satya = GrainFactory.GetGrain<IChatUserGrain>("@SatyaNadella");
            var room = GrainFactory.GetGrain<IChatRoomGrain>("#orleans");

            await jeff.JoinRoom("#orleans");

            await room.SendMessage("Hi I'm Jeff!");
            var messages = await jeff.MessagesSince(0, "#orleans");
            Assert.Equal(1, messages.Count);
        }
    }
}
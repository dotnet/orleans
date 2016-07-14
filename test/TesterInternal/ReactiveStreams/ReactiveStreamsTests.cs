using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.ReactiveStreams
{
    using Orleans.Serialization;

    public class ReactiveStreamsTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional")]
        public async Task AdHocStreamTest()
        {
            var grain = GrainFactory.GetGrain<IReactiveGrain<int>>(Guid.NewGuid());
            var expected = new[] { 1, 2, 3, 4, 5, 6 };
            var stream = grain.GetStream(expected);
            var observer = new BufferedObserver<int>();
            var disposable = await stream.SubscribeAsync(observer);
            await disposable.UnsubscribeAsync();
            Assert.Equal(expected, observer.Buffer);
        }

        [Fact, TestCategory("Functional")]
        public async Task AdHocStreamTestWithStrings()
        {
            var grain = GrainFactory.GetGrain<IReactiveGrain<string>>(Guid.NewGuid());
            var expected = new[] { "always", "leave", "a", "note" };
            var stream = grain.GetStream(expected);
            var observer = new BufferedObserver<string>();
            var disposable = await stream.SubscribeAsync(observer);
            await disposable.UnsubscribeAsync();
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
            var proxySubscription = await reactiveGrain.JoinChatRoom("#orleans").SubscribeAsync(messagesFromProxyGrain);

            // Join the room directly.
            Assert.Equal(1, await roomGrain.GetCurrentUserCount());
            var messagesFromRoom = new BufferedObserver<string>();
            var subscription = await roomGrain.JoinRoom().SubscribeAsync(messagesFromRoom);
            Assert.Equal(2, await roomGrain.GetCurrentUserCount());

            // Send a couple of messages to the room.
            var messages = new[] { "These things are fun", "Issue #940 needs some attention..." };
            await roomGrain.SendMessage(messages[0]);
            await roomGrain.SendMessage(messages[1]);

            // Leave the room by disposing one of the subscriptions.
            await proxySubscription.UnsubscribeAsync();
            Assert.Equal(1, await roomGrain.GetCurrentUserCount());

            // Send another message.
            await roomGrain.SendMessage(messages[1].ToUpperInvariant());

            // Check that the observers received the right messages.
            Assert.Equal(messages, messagesFromProxyGrain.Buffer);
            await subscription.UnsubscribeAsync();
            Assert.Equal(new[] { messages[0], messages[1], messages[1].ToUpperInvariant() }, messagesFromRoom.Buffer);
            Assert.Equal(0, await roomGrain.GetCurrentUserCount());
            await roomGrain.SendMessage("NO ONE WILL RECEIVE THIS!");
        }

        [Fact, TestCategory("Functional")]
        public async Task GrainToGrainAdhocStreamingTest()
        {
            var jeff = GrainFactory.GetGrain<IChatUserGrain>("@JeffBezos");
            var room = GrainFactory.GetGrain<IChatRoomGrain>("#orleans");

            await jeff.JoinRoom("#orleans");

            await room.SendMessage("Hi I'm Jeff!");
            var messages = await jeff.MessagesSince(0, "#orleans");
            var originalLifetime = await jeff.GetLifetimeId();
            Assert.Equal(1, messages.Count);
            await jeff.Deactivate();
            await room.SendMessage("WAKE UP, JEFF!");
            messages = await jeff.MessagesSince(0, "#orleans");
            Assert.Equal(2, messages.Count);

            // Ensure that the grain was actually reactivated;
            Assert.NotEqual(originalLifetime, await jeff.GetLifetimeId());
        }

        [Fact, TestCategory("Functional")]
        public async Task ClientResumeAsync()
        {
            var room = GrainFactory.GetGrain<IChatRoomGrain>("#orleans");

            // Subscribe and send a message.
            var first = new BufferedObserver<string>();
            var observable = room.JoinRoom();
            observable = SerializationManager.RoundTripSerializationForTesting(observable);
            var handle = await observable.SubscribeAsync(first);
            await room.SendMessage("one!");

            // Round-trip the handle through serialization and resume with a new observer.
            handle = SerializationManager.RoundTripSerializationForTesting(handle);
            var second = new BufferedObserver<string>();
            await handle.ResumeAsync(second);
            await room.SendMessage("two!");

            Assert.Equal(new[] { "one!" }, first.Buffer);
            Assert.Equal(new[] { "two!" }, second.Buffer);
            await handle.UnsubscribeAsync();
        }

        // TODO: Tests:
        // Grain call OnNext for deactivated grain which does *not* called ResumeAsync on reactivation.
        // Client detects remote endpoint crash
        // Grain detects remote client crash (note grain observer crashes should not result in an error or removal of the observer)
        // Round-trip all serializable types
        // Reentrancy tests
        // Fault injection tests
    }
}
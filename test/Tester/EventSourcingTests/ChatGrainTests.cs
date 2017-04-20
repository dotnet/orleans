using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime;
using System.Xml.Linq;
using System.IO;
using TestGrains;

namespace Tester.EventSourcingTests
{
    public class ChatGrainTests : IClassFixture<EventSourcingClusterFixture>
    {
        private readonly EventSourcingClusterFixture fixture;

        public ChatGrainTests(EventSourcingClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task Init()
        {
            var chat = this.fixture.GrainFactory.GetGrain<IChatGrain>($"Chatroom-{Guid.NewGuid()}");

            var content = (await chat.GetChat()).ToString();

            var expectedprefix = "<!--This chat room was created by TestGrains.ChatGrain-->\r\n<root>\r\n  <created>";
            var expectedsuffix = "</created>\r\n  <posts />\r\n</root>";
 
            Assert.True(content.StartsWith(expectedprefix));
            Assert.True(content.EndsWith(expectedsuffix));
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task PostThenDelete()
        {
            var chat = this.fixture.GrainFactory.GetGrain<IChatGrain>($"Chatroom-{Guid.NewGuid()}");
            var guid = Guid.NewGuid();

            await chat.Post(guid, "Famous Athlete", "I am retiring");

            {
                var content = (await chat.GetChat()).ToString();
                var doc = XDocument.Load(new StringReader(content));
                var container = doc.GetPostsContainer();
                Assert.Equal(1, container.Elements("post").Count());
            }

            await chat.Delete(guid);

            {
                var content = (await chat.GetChat()).ToString();
                var doc = XDocument.Load(new StringReader(content));
                var container = doc.GetPostsContainer();
                Assert.Equal(0, container.Elements("post").Count());
            }
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task PostThenEdit()
        {
            var chat = this.fixture.GrainFactory.GetGrain<IChatGrain>($"Chatroom-{Guid.NewGuid()}");
            var guid = Guid.NewGuid();

            await chat.Post(Guid.NewGuid(), "asdf", "asdf");
            await chat.Post(guid, "Famous Athlete", "I am retiring");
            await chat.Post(Guid.NewGuid(), "456", "456");

            {
                var content = (await chat.GetChat()).ToString();
                var doc = XDocument.Load(new StringReader(content));
                var container = doc.GetPostsContainer();
                Assert.Equal(3, container.Elements("post").Count());
            }

            await chat.Edit(guid, "I am not retiring");

            {
                var content = (await chat.GetChat()).ToString();
                var doc = XDocument.Load(new StringReader(content));
                var container = doc.GetPostsContainer();
                Assert.Equal(3, container.Elements("post").Count());
                var post = doc.FindPost(guid.ToString());
                Assert.Equal("I am not retiring", post.Element("text").Value);
            }
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task Truncate()
        {
            var chat = this.fixture.GrainFactory.GetGrain<IChatGrain>($"Chatroom-{Guid.NewGuid()}");

            for (int i = 0; i < ChatFormat.MaxNumPosts + 10; i++)
                await chat.Post(Guid.NewGuid(), i.ToString(), i.ToString());

            {
                var content = (await chat.GetChat()).ToString();
                var doc = XDocument.Load(new StringReader(content));
                var container = doc.GetPostsContainer();
                Assert.Equal(ChatFormat.MaxNumPosts, container.Elements("post").Count());
            }
        }

    }
}
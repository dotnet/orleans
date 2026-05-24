using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Messaging;
using Xunit;

namespace Orleans.Core.Tests.Networking
{
    [Trait("Category", "BVT")]
    public class ConnectionMiddlewareTests
    {
        [Fact]
        public async Task UseMiddleware_InvokesMiddlewareAndNext()
        {
            var callOrder = new List<string>();

            var services = new ServiceCollection().BuildServiceProvider();
            var builder = new TestConnectionBuilder(services);

            builder.UseMiddleware(new TrackingMiddleware("first", callOrder));
            builder.UseMiddleware(new TrackingMiddleware("second", callOrder));

            var pipeline = builder.Build();
            var context = CreateContext();

            await pipeline(context);

            Assert.Equal(new[] { "first", "second" }, callOrder);
        }

        [Fact]
        public async Task UseMiddleware_MiddlewareThatDoesNotCallNext_TerminatesPipeline()
        {
            var callOrder = new List<string>();

            var services = new ServiceCollection().BuildServiceProvider();
            var builder = new TestConnectionBuilder(services);

            builder.UseMiddleware(new TerminatingMiddleware("blocker", callOrder));
            builder.UseMiddleware(new TrackingMiddleware("should-not-run", callOrder));

            var pipeline = builder.Build();
            var context = CreateContext();

            await pipeline(context);

            Assert.Equal(new[] { "blocker" }, callOrder);
        }

        [Fact]
        public async Task UseMiddleware_Generic_ResolvesFromDI()
        {
            var callOrder = new List<string>();

            var services = new ServiceCollection()
                .AddSingleton(callOrder)
                .BuildServiceProvider();

            var builder = new TestConnectionBuilder(services);
            builder.UseMiddleware<DiMiddleware>();

            var pipeline = builder.Build();
            var context = CreateContext();

            await pipeline(context);

            Assert.Equal(new[] { "di-middleware" }, callOrder);
        }

        private static ConnectionContext CreateContext()
        {
            var pipe = new Pipe();
            return new TestConnectionContext(pipe);
        }

        private sealed class TrackingMiddleware : IConnectionMiddleware
        {
            private readonly string _name;
            private readonly List<string> _callOrder;

            public TrackingMiddleware(string name, List<string> callOrder)
            {
                _name = name;
                _callOrder = callOrder;
            }

            public async Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
            {
                _callOrder.Add(_name);
                await next(context);
            }
        }

        private sealed class TerminatingMiddleware : IConnectionMiddleware
        {
            private readonly string _name;
            private readonly List<string> _callOrder;

            public TerminatingMiddleware(string name, List<string> callOrder)
            {
                _name = name;
                _callOrder = callOrder;
            }

            public Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
            {
                _callOrder.Add(_name);
                // Intentionally NOT calling next
                return Task.CompletedTask;
            }
        }

        private sealed class DiMiddleware : IConnectionMiddleware
        {
            private readonly List<string> _callOrder;

            public DiMiddleware(List<string> callOrder)
            {
                _callOrder = callOrder;
            }

            public async Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
            {
                _callOrder.Add("di-middleware");
                await next(context);
            }
        }

        private sealed class TestConnectionBuilder : IConnectionBuilder
        {
            private readonly List<Func<ConnectionDelegate, ConnectionDelegate>> _middlewares = new();

            public TestConnectionBuilder(IServiceProvider services)
            {
                ApplicationServices = services;
            }

            public IServiceProvider ApplicationServices { get; }

            public IConnectionBuilder Use(Func<ConnectionDelegate, ConnectionDelegate> middleware)
            {
                _middlewares.Add(middleware);
                return this;
            }

            public ConnectionDelegate Build()
            {
                ConnectionDelegate app = _ => Task.CompletedTask;

                for (var i = _middlewares.Count - 1; i >= 0; i--)
                {
                    app = _middlewares[i](app);
                }

                return app;
            }
        }

        private sealed class TestConnectionContext : ConnectionContext
        {
            private readonly IDuplexPipe _transport;

            public TestConnectionContext(Pipe pipe)
            {
                _transport = new DuplexPipe(pipe.Reader, pipe.Writer);
            }

            public override string ConnectionId { get; set; } = Guid.NewGuid().ToString();
            public override IDuplexPipe Transport { get => _transport; set => throw new NotSupportedException(); }
            public override IFeatureCollection Features { get; } = new FeatureCollection();
            public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

            private sealed class DuplexPipe : IDuplexPipe
            {
                public DuplexPipe(PipeReader input, PipeWriter output)
                {
                    Input = input;
                    Output = output;
                }

                public PipeReader Input { get; }
                public PipeWriter Output { get; }
            }

            private sealed class FeatureCollection : IFeatureCollection
            {
                public object this[Type key] { get => null; set { } }
                public bool IsReadOnly => false;
                public int Revision => 0;
                public TFeature Get<TFeature>() => default;
                public void Set<TFeature>(TFeature instance) { }
                public IEnumerator<KeyValuePair<Type, object>> GetEnumerator()
                    => Enumerable.Empty<KeyValuePair<Type, object>>().GetEnumerator();
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
        }
    }
}

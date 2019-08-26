using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Networking.Shared
{
    internal class ConnectionBuilder : IConnectionBuilder
    {
        private readonly IList<Func<ConnectionDelegate, ConnectionDelegate>> _components = new List<Func<ConnectionDelegate, ConnectionDelegate>>();

        public IServiceProvider ApplicationServices { get; }

        public ConnectionBuilder(IServiceProvider applicationServices)
        {
            ApplicationServices = applicationServices;
        }

        public IConnectionBuilder Use(Func<ConnectionDelegate, ConnectionDelegate> middleware)
        {
            _components.Add(middleware);
            return this;
        }

        public ConnectionDelegate Build()
        {
            ConnectionDelegate app = features =>
            {
                return Task.CompletedTask;
            };

            foreach (var component in _components.Reverse())
            {
                app = component(app);
            }

            return app;
        }
    }
}

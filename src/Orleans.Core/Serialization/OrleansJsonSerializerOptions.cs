using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Orleans.Serialization
{
    public class OrleansJsonSerializerOptions
    {
        private readonly IList<Action<JsonSerializerSettings>> actions = new List<Action<JsonSerializerSettings>>();

        public void ConfigureSerializerSettings(Action<JsonSerializerSettings> configure) => this.actions.Add(configure);

        internal void ConfigureSerializer(JsonSerializerSettings jsonSerializerSettings)
        {
            foreach (var action in this.actions)
            {
                action(jsonSerializerSettings);
            }
        }
    }
}

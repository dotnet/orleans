using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Streams
{
    public class RabbitMQOptionsValidator : IConfigurationValidator
    {
        private RabbitMQOptions rabbitMQOptions;
        private string name;

        // undone (mxplusb): finish this.
        public RabbitMQOptionsValidator(RabbitMQOptions rabbitMQOptions, string name)
        {
            this.rabbitMQOptions = rabbitMQOptions;
            this.name = name;
        }
    }
}

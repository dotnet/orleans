using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    internal class CollectionAgeLimitValidator : IConfigurationValidator
    {
        private IOptions<GrainCollectionOptions> options;

        public CollectionAgeLimitValidator(IOptions<GrainCollectionOptions> options)
        {
            this.options = options;
        }

        public void ValidateConfiguration()
        {
            foreach(var classSpecificCollectionAge in this.options.Value.ClassSpecificCollectionAge)
            {
                if(classSpecificCollectionAge.Value <= TimeSpan.Zero)
                {
                    throw new OrleansConfigurationException($"{classSpecificCollectionAge.Key} CollectionAgeLimit is set to {classSpecificCollectionAge.Value}. CollectionAgeLimit must be greater than zero.");
                }
            }
        }
    }
}

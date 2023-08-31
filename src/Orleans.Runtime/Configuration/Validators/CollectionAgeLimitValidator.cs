using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    internal class GrainCollectionOptionsValidator : IConfigurationValidator
    {
        private readonly IOptions<GrainCollectionOptions> options;

        public GrainCollectionOptionsValidator(IOptions<GrainCollectionOptions> options)
        {
            this.options = options;
        }

        public void ValidateConfiguration()
        {
            if (options.Value.CollectionQuantum <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.CollectionQuantum)} is set to {options.Value.CollectionQuantum}. " +
                    $"{nameof(GrainCollectionOptions.CollectionQuantum)} must be greater than 0");
            }

            if (options.Value.CollectionAge <= options.Value.CollectionQuantum)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(GrainCollectionOptions.CollectionAge)} is set to {options.Value.CollectionAge}. " +
                    $"{nameof(GrainCollectionOptions.CollectionAge)} must be greater than {nameof(GrainCollectionOptions.CollectionQuantum)}, " +
                    $"which is set to {options.Value.CollectionQuantum}");
            }
            foreach(var classSpecificCollectionAge in options.Value.ClassSpecificCollectionAge)
            {
                if (classSpecificCollectionAge.Value <= options.Value.CollectionQuantum)
                {
                    throw new OrleansConfigurationException(
                        $"{classSpecificCollectionAge.Key} CollectionAgeLimit is set to {classSpecificCollectionAge.Value}. " +
                        $"CollectionAgeLimit must be greater than {nameof(GrainCollectionOptions.CollectionQuantum)}, " +
                        $"which is set to {options.Value.CollectionQuantum}");
                }
            }
        }
    }
}

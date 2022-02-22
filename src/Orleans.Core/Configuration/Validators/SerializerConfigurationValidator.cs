using System;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;

namespace Orleans
{
    /// <summary>
    /// Validates serializer configuration.
    /// </summary>
    public class SerializerConfigurationValidator : IConfigurationValidator
    {
        private ICodecProvider _codecProvider;
        private TypeManifestOptions _options;
        private bool _enabled;

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerConfigurationValidator"/> class.
        /// </summary>
        /// <param name="codecProvider">
        /// The codec provider.
        /// </param>
        /// <param name="options">
        /// The type manifest options.
        /// </param>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
        public SerializerConfigurationValidator(ICodecProvider codecProvider, IOptions<TypeManifestOptions> options, IServiceProvider serviceProvider)
        {
            _codecProvider = codecProvider;
            _options = options.Value;

            var configEnabled = _options.EnableConfigurationAnalysis;
            if (configEnabled.HasValue)
            {
                _enabled = configEnabled.Value;
            }
            else
            {
                // Enable in development envioronment by default.
                var environment = serviceProvider.GetService<IHostEnvironment>();
                _enabled = environment is not null && environment.IsDevelopment();
            }
        }

        void IConfigurationValidator.ValidateConfiguration()
        {
            if (!_enabled)
            {
                return;
            }

            var complaints = SerializerConfigurationAnalyzer.AnalyzeSerializerAvailability(_codecProvider, _options);
            if (complaints.Count > 0)
            {
                var result = new StringBuilder();
                result.AppendLine("Found unserializable or uncopyable types which are being referenced in grain interface signatures:");
                foreach (var (type, complaint) in complaints)
                {
                    result.Append($"Type: {type}");
                    if (!complaint.HasSerializer)
                    {
                        result.Append(" has no serializer");
                        if (!complaint.HasCopier)
                        {
                            result.Append(" or copier");
                        }
                    }
                    else if (!complaint.HasCopier)
                    {
                        result.Append(" has no copier");
                    }

                    result.Append(" and was referenced by the following:");
                    foreach (var (declaringType, methodList) in complaint.Methods)
                    {
                        result.Append($"\n\t* {RuntimeTypeNameFormatter.Format(declaringType)} methods: {string.Join(", ", methodList)}");
                    }

                    result.AppendLine();
                }

                result.AppendLine("Ensure that all types which are used in grain interfaces have serializers available for them.");
                result.AppendLine("Applying the [GenerateSerializer] attribute to your type and adding [Id(x)] attributes to serializable properties and fields is the simplest way to accomplish this.");
                result.AppendLine("Alternatively, for types which are outside of your control, serializers may have to be manually crafted, potentially using surrogate types.");

                throw new OrleansConfigurationException(result.ToString());
            }
        }
    }
}
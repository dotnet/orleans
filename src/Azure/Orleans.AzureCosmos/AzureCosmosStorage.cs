using System;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.AzureCosmos
{
    internal abstract class AzureCosmosStorage
    {
        protected readonly ILogger logger;
        protected Container container;

        protected AzureCosmosStorage(ILoggerFactory loggerFactory) => logger = loggerFactory.CreateLogger(GetType());

        protected async Task Init(StorageOptionsBase options, ContainerProperties properties)
        {
            try
            {
                var db = options.Connection().GetDatabase(options.DatabaseName);
                var container = db.GetContainer(options.ContainerName);

                bool create;
                var startTime = DateTime.UtcNow;
                using (var res = await container.ReadContainerStreamAsync())
                {
                    CheckAlertSlowAccess(startTime, "ReadContainer");
                    if (!(create = res.StatusCode == HttpStatusCode.NotFound))
                        res.EnsureSuccessStatusCode();
                }

                if (create)
                {
                    properties.Id = options.ContainerName;
                    startTime = DateTime.UtcNow;
                    using var res = await db.CreateContainerStreamAsync(properties);
                    CheckAlertSlowAccess(startTime, "CreateContainer", 5);

                    create = res.StatusCode != HttpStatusCode.Conflict;
                    if (create) res.EnsureSuccessStatusCode();
                }

                logger.Info("{0} Azure Cosmos container {1}", create ? "Created" : "Attached to", options.ContainerName);
                this.container = container;
            }
            catch (Exception ex) when (Log(ex, $"Error connecting to Azure Cosmos container {options.ContainerName}")) { throw; }
        }

        protected void CheckAlertSlowAccess(DateTime startOperation, string operation, int multiplier = 1)
        {
            var duration = DateTime.UtcNow - startOperation;
            if (duration.Ticks > 3 * TimeSpan.TicksPerSecond * multiplier)
                logger.LogWarning("Slow access to Azure Cosmos container {0} for {1}, which took {2}.", container.Id, operation, duration);
        }

        protected bool Log(Exception ex, string msg = null, [CallerMemberName] string memberName = "")
        {
            logger.LogWarning(ex, msg ?? $"{memberName} failed");
            return false;
        }

        protected static readonly ItemRequestOptions noContentResponse = new() { EnableContentResponseOnWrite = false };

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        protected static T Deserialize<T>(ResponseMessage res)
        {
            switch (res.Content)
            {
                case MemoryStream ms when ms.TryGetBuffer(out var buf):
                    return JsonSerializer.Deserialize<T>(buf);
                case var stream:
                    var t = JsonSerializer.DeserializeAsync<T>(stream);
                    return t.IsCompleted ? t.Result : t.AsTask().GetAwaiter().GetResult();
            }
        }

        protected abstract class RecordBase
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("_etag")]
            public string ETag { get; set; }

            public MemoryStream Serialize()
            {
                var buf = JsonSerializer.SerializeToUtf8Bytes<object>(this, SerializerOptions);
                return new MemoryStream(buf, 0, buf.Length, false, true);
            }
        }
    }
}

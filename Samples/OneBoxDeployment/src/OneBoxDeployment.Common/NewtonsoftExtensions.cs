using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace OneBoxDeployment.Common
{
    /// <summary>
    /// Extends Newtonsoft.JSON libraries.
    /// </summary>
    public static class NewtonsoftJsonExtensions
    {
        /// <summary>
        /// Reads the contents as a string stream and deserializes the result.
        /// </summary>
        /// <typeparam name="TResult">The result </typeparam>
        /// <param name="content">The HTTP content from which to deserialize.</param>
        /// <returns>Upon success the deserialized object.</returns>
        /// <remarks>Assumes the result is JSON and UTF-8 encoded.</remarks>
        public static async Task<TResult> DeserializeAsync<TResult>(this HttpContent content)
        {
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                using (var streamReader = new StreamReader(stream))
                {
                    using (var reader = new JsonTextReader(streamReader))
                    {
                        var serializer = new JsonSerializer();
                        return serializer.Deserialize<TResult>(reader);
                    }
                }
            }
        }


        /// <summary>
        /// Creates a new copy of the <paramref name="reader"/> to the given <paramref name="jObject"/>.
        /// </summary>
        /// <param name="jObject">The <see cref="JObject"/> for which to copy the reader settings.</param>
        /// <param name="reader">The reader from which to copy.</param>
        /// <returns></returns>
        public static JsonReader CopyReaderForObject(this JObject jObject, JsonReader reader)
        {
            var jObjectReader = jObject.CreateReader();
            jObjectReader.Culture = reader.Culture;
            jObjectReader.DateFormatString = reader.DateFormatString;
            jObjectReader.DateParseHandling = reader.DateParseHandling;
            jObjectReader.DateTimeZoneHandling = reader.DateTimeZoneHandling;
            jObjectReader.FloatParseHandling = reader.FloatParseHandling;
            jObjectReader.MaxDepth = reader.MaxDepth;
            jObjectReader.SupportMultipleContent = reader.SupportMultipleContent;

            return jObjectReader;
        }
    }
}

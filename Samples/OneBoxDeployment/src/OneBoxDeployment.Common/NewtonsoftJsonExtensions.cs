using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace OneBoxDeployment.Common
{
    /// <summary>
    /// Extends Newtonsoft.JSON and HTTP types.
    /// </summary>
    public static class NewtonsoftJsonExtensions
    {
        /// <summary>
        /// The serializer used in all operations.
        /// </summary>
        private static JsonSerializer Serializer => new JsonSerializer();

        /// <summary>
        /// The default content type.
        /// </summary>
        public const string DefaultContentType = "application/json";


        /// <summary>
        /// Writes UTF-8 encoded JSON into <paramref name="response"/>.
        /// </summary>
        /// <typeparam name="T">The type of the response object.</typeparam>
        /// <param name="response">The HTTP response.</param>
        /// <param name="obj">The response object written into <paramref name="response"/>.</param>
        /// <param name="contentType">The content type of the response. The content type, by default <see cref="DefaultContentType"/>.</param>
        public static void WriteJson<T>(this HttpResponse response, T obj, string contentType = DefaultContentType)
        {
            response.ContentType = contentType;
            using(var writer = new HttpResponseStreamWriter(response.Body, Encoding.UTF8))
            {
                using(var jsonWriter = new JsonTextWriter(writer) { CloseOutput = false, AutoCompleteOnClose = false })
                {
                    Serializer.Serialize(jsonWriter, obj);
                }
            }
        }


        /// <summary>
        /// Reads the contents as a string stream and deserializes the result.
        /// </summary>
        /// <typeparam name="TResult">The result </typeparam>
        /// <param name="content">The HTTP content from which to deserialize.</param>
        /// <returns>Upon success the deserialized object.</returns>
        /// <remarks>Assumes the result is JSON and UTF-8 encoded.</remarks>
        public static async Task<TResult> DeserializeAsync<TResult>(this HttpContent content)
        {
            using(var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                using(var streamReader = new StreamReader(stream))
                {
                    using(var reader = new JsonTextReader(streamReader))
                    {
                        return Serializer.Deserialize<TResult>(reader);
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

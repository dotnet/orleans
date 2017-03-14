using Orleans;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TestGrainInterfaces
{
    /// <summary>
    /// The grain interface for the chat grain.
    /// </summary>
    public interface IChatGrain : IGrainWithStringKey
    {
        /// <summary> Return the current content of the chat room. </summary>
        Task<XDocument> GetChat();

        /// <summary> Add a new post. </summary>
        Task Post(Guid guid, string user, string text);

        /// <summary> Delete a specific post. </summary>
        Task Delete(Guid guid);

        /// <summary> Edit a specific post. </summary>
        Task Edit(Guid guid, string text);

    }


    /// <summary>
    /// Since XDocument does not seem to serialize automatically, we provide the necessary methods
    /// </summary>
    [Serializer(typeof(XDocument))]
    public class XDocumentSerialization
    {
        [CopierMethod]
        public static object DeepCopier(object original, ICopyContext context)
        {
            return new XDocument((XDocument)original);
        }

        [SerializerMethod]
        public static void Serialize(object untypedInput, ISerializationContext context, Type expected)
        {
            var document = (XDocument)untypedInput;
            var stream = context.StreamWriter;
            stream.Write(document.ToString());
        }

        [DeserializerMethod]
        public static object Deserialize(Type expected, IDeserializationContext context)
        {
            var stream = context.StreamReader;
            var text = stream.ReadString();
            return XDocument.Load(new StringReader(text));
        }

    }

}

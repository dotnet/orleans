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
}

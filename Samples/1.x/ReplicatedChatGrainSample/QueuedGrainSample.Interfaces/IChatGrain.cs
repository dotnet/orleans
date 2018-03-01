using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace ReplicatedChatGrainSample.Interfaces
{
    public interface IChatGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Append a message to the chat.
        /// </summary>
        Task<LocalState> AppendMessage(string messageText);

        /// <summary>
        /// Clear all messages from the chat.
        /// </summary>
        Task<LocalState> ClearAll();


        /// <summary>
        /// For demonstration purposes, we include this grain method
        /// which returns the current local state of the grain
        /// </summary>
        /// <returns></returns>
        Task<LocalState> GetLocalState();
    }

    [Serializable]
    public class LocalState
    {
        public List<string> TentativeState { get; set; }

        public List<string> ConfirmedState { get; set; }

        public List<string> UnconfirmedEvents { get; set; }
    }
}
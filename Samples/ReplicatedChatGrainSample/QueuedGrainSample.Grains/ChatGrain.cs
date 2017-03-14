using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.EventSourcing;
using Orleans.Providers;
using ReplicatedChatGrainSample.Interfaces;

namespace ReplicatedChatGrainSample.Grains
{
    [Serializable]
    public class ChatState
    {
        public ChatState()
        {
            this.Messages = new List<string>();
        }

        public List<string> Messages { get; set; }

        public void Apply(AppendMessageEvent e)
        {
            this.Messages.Add(e.Message);
        }

        public void Apply(ClearAllMessagesEvent e)
        {
            this.Messages.Clear();
        }
    }


    [Serializable]
    public class AppendMessageEvent
    {
        public string Message { get; set; }

        public override string ToString()
        {
            return string.Format("[AppendMessage \"{0}\"]", this.Message);
        }
    }

    [Serializable]
    public class ClearAllMessagesEvent
    {
        public override string ToString()
        {
            return string.Format("[ClearAllMessages]");
        }
    }


    [StorageProvider(ProviderName = "GloballySharedAzureAccount")]
    public class ChatGrain : JournaledGrain<ChatState>, IChatGrain
    {
        public Task<LocalState> AppendMessage(string msg)
        {
            RaiseEvent(new AppendMessageEvent() { Message = msg });
            return GetLocalState();
        }

        public Task<LocalState> ClearAll()
        {
            RaiseEvent(new ClearAllMessagesEvent());
            return GetLocalState();
        }

        public Task<LocalState> GetLocalState()
        {
            return Task.FromResult(new LocalState()
            {
                TentativeState = this.TentativeState.Messages,
                ConfirmedState = this.State.Messages,
                UnconfirmedEvents = this.UnconfirmedEvents.Select((u) => u.ToString()).ToList()
            });
        }
    }
}
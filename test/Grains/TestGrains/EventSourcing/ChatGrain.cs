using Orleans.EventSourcing;
using Orleans.Providers;
using System;
using System.Threading.Tasks;
using System.Xml.Linq;
using TestGrainInterfaces;

namespace TestGrains
{
    /// <summary>
    /// An example of a journaled grain implementing a chat.
    /// The state of the grain is an XML document (System.Xml.Linq.XDocument).
    /// 
    /// Configured to use the default storage provider.
    /// Configured to use the LogStorage consistency provider.
    /// 
    /// This means we persist all events; since events are replayed when a grain is loaded 
    /// we can change the XML schema later
    /// 
    /// </summary>

    [StorageProvider(ProviderName = "Default")]
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    public class ChatGrain : JournaledGrain<XDocument, IChatEvent>, IChatGrain
    {
        // we want to ensure chats are correctly initialized when first used
        // so we override the default activation, to insert a creation event if needed
        public override async Task OnActivateAsync()
        {
            // first, wait for all events to be loaded from storage so we are caught up with the latest version
            await base.OnActivateAsync();

            // if the chat has not been initialized, do that now
            if (Version == 0)
            {
                // we are using a conditional event (in case creation is racing with other clusters)
                // (conditional events commit only if the version hasn't already changed in the meantime)
                await RaiseConditionalEvent(new CreatedEvent()
                {
                    Timestamp = DateTime.UtcNow,
                    Origin = typeof(ChatGrain).FullName
                });
            }
        }

        /// <summary>
        /// in order to apply events correctly to the grain state, we 
        /// must override the transition function
        /// (because an XDocument object does not have an Apply function)
        /// </summary>
        protected override void TransitionState(XDocument state, IChatEvent @event)
        {
            @event.Update(state);
        }

        public Task<XDocument> GetChat()
        {
            return Task.FromResult(TentativeState);
        }
 
        public Task Post(Guid guid, string user, string text)
        {
            RaiseEvent(new PostedEvent() { Guid = guid, User = user, Text = text, Timestamp = DateTime.UtcNow });
            return Task.CompletedTask;
        }

        public Task Delete(Guid guid)
        {
            RaiseEvent(new DeletedEvent() { Guid = guid });
            return Task.CompletedTask;
        }

        public Task Edit(Guid guid, string text)
        {
            RaiseEvent(new EditedEvent() { Guid = guid, Text = text});
            return Task.CompletedTask;
        }
    }
}


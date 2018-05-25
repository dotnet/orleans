using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using TestGrainInterfaces;

namespace TestGrains
{
    /// <summary>
    /// all chat events implement this interface, to define how each event changes the XML document
    /// </summary>
    public interface IChatEvent
    {
        void Update(XDocument document);
    }

    [Serializable]
    public class CreatedEvent : IChatEvent
    {
        public DateTime Timestamp { get; set; }
        public string Origin { get; set; }

        public void Update(XDocument document)
        {
            document.Initialize(Timestamp, Origin);
        }
    }


    [Serializable]
    public class PostedEvent : IChatEvent
    {
        public Guid Guid { get; set; }
        public string User { get; set; }
        public DateTime Timestamp { get; set; }
        public string Text { get; set; }

        public void Update(XDocument document)
        {
            var container = document.GetPostsContainer();
            container.Add(ChatFormat.MakePost(Guid, User, Timestamp, Text));
            document.EnforceLimit();
        }
    }

    [Serializable]
    public class DeletedEvent : IChatEvent
    {
        public Guid Guid { get; set; }

        public void Update(XDocument document)
        {
            document.FindPost(Guid.ToString())?.Remove();
        }
    }

    [Serializable]
    public class EditedEvent : IChatEvent
    {
        public Guid Guid { get; set; }
        public string Text { get; set; }

        public void Update(XDocument document)
        {
            document.FindPost(Guid.ToString())?.ReplaceText(Text);
        }
    }
}
   
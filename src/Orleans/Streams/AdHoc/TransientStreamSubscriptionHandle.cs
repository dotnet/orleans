namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading.Tasks;

    [Serializable]
    internal class TransientStreamSubscriptionHandle<T> : StreamSubscriptionHandle<T>
    {
        private readonly Guid streamId;

        private readonly GrainObservableProxy<T> observable;

        [SuppressMessage("ReSharper", "NotAccessedField.Local", Justification = "This field prevents the reference from being garbage collected.")]
        [NonSerialized]
        private readonly object observerReference;

        public TransientStreamSubscriptionHandle(Guid streamId, GrainObservableProxy<T> observable, object observerReference)
        {
            this.streamId = streamId;
            this.observable = observable;
            this.observerReference = observerReference;
        }

        public override IStreamIdentity StreamIdentity => new TransientStreamIdentity(this.streamId);

        public override Guid HandleId => this.streamId;

        public override Task UnsubscribeAsync()
        {
            return this.observable.GrainExtension.Unsubscribe(this.streamId);
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null)
        {
            return this.observable.ResumeAsync(this.streamId, observer, token);
        }

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            return this.HandleId == other.HandleId;
        }

        public class TransientStreamIdentity : IStreamIdentity
        {
            public TransientStreamIdentity(Guid id)
            {
                this.Guid = id;
            }

            public Guid Guid { get; }

            public string Namespace => string.Empty;
        }
    }
}
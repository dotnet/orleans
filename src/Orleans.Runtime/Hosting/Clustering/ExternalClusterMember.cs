namespace Orleans.Runtime.Hosting.Clustering
{
    public class ExternalClusterMember
    {
        public string Name { get; }

        public string Description { get; }

        public bool IsCurrentSilo { get; set; }

        public ExternalClusterMember(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public override string ToString()
        {
            return Description;
        }
    }

    public sealed class ClusterMemberDeleted : ClusterEvent
    {
        public ClusterMemberDeleted(ExternalClusterMember member)
            : base(member)
        {
        }
    }

    public abstract class ClusterEvent
    {
        public ExternalClusterMember Member { get; }

        protected ClusterEvent(ExternalClusterMember member)
        {
            Member = member;
        }
    }
}

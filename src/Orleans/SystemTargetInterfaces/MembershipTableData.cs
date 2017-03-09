using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Orleans.Runtime;

namespace Orleans
{
    [Serializable]
    public class MembershipTableData
    {
        public IReadOnlyList<Tuple<MembershipEntry, string>> Members { get; private set; }

        public TableVersion Version { get; private set; }

        public MembershipTableData(List<Tuple<MembershipEntry, string>> list, TableVersion version)
        {
            // put deads at the end, just for logging.
            list.Sort(
                (x, y) =>
                {
                    if (x.Item1.Status == SiloStatus.Dead) return 1; // put Deads at the end
                    if (y.Item1.Status == SiloStatus.Dead) return -1; // put Deads at the end
                    return String.Compare(x.Item1.SiloName, y.Item1.SiloName, StringComparison.Ordinal);
                });
            Members = list.AsReadOnly();
            Version = version;
        }

        public MembershipTableData(Tuple<MembershipEntry, string> tuple, TableVersion version)
        {
            Members = (new List<Tuple<MembershipEntry, string>> { tuple }).AsReadOnly();
            Version = version;
        }

        public MembershipTableData(TableVersion version)
        {
            Members = (new List<Tuple<MembershipEntry, string>>()).AsReadOnly();
            Version = version;
        }

        public Tuple<MembershipEntry, string> Get(SiloAddress silo)
        {
            return Members.First(tuple => tuple.Item1.SiloAddress.Equals(silo));
        }

        public bool Contains(SiloAddress silo)
        {
            return Members.Any(tuple => tuple.Item1.SiloAddress.Equals(silo));
        }

        public override string ToString()
        {
            int active = Members.Count(e => e.Item1.Status == SiloStatus.Active);
            int dead = Members.Count(e => e.Item1.Status == SiloStatus.Dead);
            int created = Members.Count(e => e.Item1.Status == SiloStatus.Created);
            int joining = Members.Count(e => e.Item1.Status == SiloStatus.Joining);
            int shuttingDown = Members.Count(e => e.Item1.Status == SiloStatus.ShuttingDown);
            int stopping = Members.Count(e => e.Item1.Status == SiloStatus.Stopping);

            string otherCounts = String.Format("{0}{1}{2}{3}",
                created > 0 ? (", " + created + " are Created") : "",
                joining > 0 ? (", " + joining + " are Joining") : "",
                shuttingDown > 0 ? (", " + shuttingDown + " are ShuttingDown") : "",
                stopping > 0 ? (", " + stopping + " are Stopping") : "");

            return string.Format("{0} silos, {1} are Active, {2} are Dead{3}, Version={4}. All silos: {5}",
                Members.Count,
                active,
                dead,
                otherCounts,
                Version,
                Utils.EnumerableToString(Members, tuple => tuple.Item1.ToFullString()));
        }

        // return a copy of the table removing all dead appereances of dead nodes, except for the last one.
        public MembershipTableData SupressDuplicateDeads()
        {
            var dead = new Dictionary<IPEndPoint, Tuple<MembershipEntry, string>>();
            // pick only latest Dead for each instance
            foreach (var next in Members.Where(item => item.Item1.Status == SiloStatus.Dead))
            {
                var ipEndPoint = next.Item1.SiloAddress.Endpoint;
                Tuple<MembershipEntry, string> prev;
                if (!dead.TryGetValue(ipEndPoint, out prev))
                {
                    dead[ipEndPoint] = next;
                }
                else
                {
                    // later dead.
                    if (next.Item1.SiloAddress.Generation.CompareTo(prev.Item1.SiloAddress.Generation) > 0)
                        dead[ipEndPoint] = next;
                }
            }
            //now add back non-dead
            List<Tuple<MembershipEntry, string>> all = dead.Values.ToList();
            all.AddRange(Members.Where(item => item.Item1.Status != SiloStatus.Dead));
            return new MembershipTableData(all, Version);
        }
    }
}
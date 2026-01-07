using System;
using System.Collections;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging
{
    internal class ConnectionLogScope(Connection connection) : IReadOnlyList<KeyValuePair<string, object>>
    {
        private string _cachedToString;

        public KeyValuePair<string, object> this[int index]
        {
            get
            {
                if (index == 0)
                {
                    return new KeyValuePair<string, object>(nameof(Connection.ConnectionId), connection.ConnectionId);
                }

                if (index == 1)
                {
                    return new KeyValuePair<string, object>(nameof(Connection.LocalEndPoint), connection.LocalEndPoint);
                }

                if (index == 2)
                {
                    return new KeyValuePair<string, object>(nameof(Connection.RemoteEndPoint), connection.RemoteEndPoint);
                }

                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public int Count => 3;

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            for (int i = 0; i < Count; ++i)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override string ToString()
        {
            if (_cachedToString == null)
            {
                _cachedToString = connection.ToString();
            }

            return _cachedToString;
        }
    }
}

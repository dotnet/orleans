using System;
using System.Collections;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging
{
    internal class ConnectionLogScope : IReadOnlyList<KeyValuePair<string, object>>
    {
        private readonly Connection _connection;

        private string _cachedToString;

        public ConnectionLogScope(Connection connection)
        {
            _connection = connection;
        }

        public KeyValuePair<string, object> this[int index]
        {
            get
            {
                if (index == 0)
                {
                    return new KeyValuePair<string, object>(nameof(Connection.ConnectionId), _connection.ConnectionId);
                }

                if (index == 1)
                {
                    return new KeyValuePair<string, object>(nameof(Connection.LocalEndPoint), _connection.LocalEndPoint);
                }

                if (index == 2)
                {
                    return new KeyValuePair<string, object>(nameof(Connection.RemoteEndPoint), _connection.RemoteEndPoint);
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
                _cachedToString = _connection.ToString();
            }

            return _cachedToString;
        }
    }
}

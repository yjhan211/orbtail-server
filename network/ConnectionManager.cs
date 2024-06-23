using System.Collections.Concurrent;

namespace network
{
    public class ConnectionManager
    {
        private ConcurrentDictionary<
            string,
            ConcurrentDictionary<UserToken, DateTime>
        > connections = new();
        private const int MAX_CONNECTIONS_PER_IP = 5;
        private const int CONNECTION_TIMEOUT_SECONDS = 30;

        public bool CanAcceptConnection(string ipAddress)
        {
            if (!connections.TryGetValue(ipAddress, out var userTokens))
            {
                return true;
            }
            return userTokens.Count < MAX_CONNECTIONS_PER_IP;
        }

        public void AddConnection(string ipAddress, UserToken userToken)
        {
            connections.AddOrUpdate(
                ipAddress,
                _ => new ConcurrentDictionary<UserToken, DateTime> { [userToken] = DateTime.Now },
                (_, dict) =>
                {
                    dict[userToken] = DateTime.Now;
                    return dict;
                }
            );
        }

        public void RemoveConnection(string ipAddress, UserToken userToken)
        {
            if (connections.TryGetValue(ipAddress, out var userTokens))
            {
                userTokens.TryRemove(userToken, out _);
                if (userTokens.IsEmpty)
                {
                    connections.TryRemove(ipAddress, out _);
                }
            }
        }

        public void UpdateActivityTime(string ipAddress, UserToken userToken)
        {
            if (connections.TryGetValue(ipAddress, out var userTokens))
            {
                userTokens[userToken] = DateTime.Now;
            }
        }

        public void CleanupTimedOutConnections(Action<UserToken> closeClientSocket)
        {
            var now = DateTime.Now;
            foreach (var kvp in connections)
            {
                var ipAddress = kvp.Key;
                var userTokens = kvp.Value;

                foreach (var userTokenKvp in userTokens)
                {
                    var userToken = userTokenKvp.Key;
                    var lastActivityTime = userTokenKvp.Value;

                    if ((now - lastActivityTime).TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
                    {
                        closeClientSocket(userToken);
                        RemoveConnection(ipAddress, userToken);
                    }
                }
            }
        }
    }
}

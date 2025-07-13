using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VirtuPathAPI.Controllers
{
    public class PresenceTracker : IPresenceTracker
    {
        // userId → set of connectionIds
        private readonly ConcurrentDictionary<int, HashSet<string>> _connections
            = new();

        public Task UserConnectedAsync(int userId, string connectionId)
        {
            var conns = _connections.GetOrAdd(userId, _ => new());
            lock (conns) { conns.Add(connectionId); }
            return Task.CompletedTask;
        }

        public Task UserDisconnectedAsync(int userId, string connectionId)
        {
            if (_connections.TryGetValue(userId, out var conns))
            {
                lock (conns) { conns.Remove(connectionId); }
                if (conns.Count == 0) _connections.TryRemove(userId, out _);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<int>> GetOnlineFriendsAsync(int userId, IEnumerable<int> allFriendIds)
        {
            var online = allFriendIds.Where(f => _connections.ContainsKey(f)).ToList();
            return Task.FromResult((IReadOnlyList<int>)online);
        }
    }
}

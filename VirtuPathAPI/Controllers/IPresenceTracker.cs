// IPresenceTracker.cs
public interface IPresenceTracker
{
    Task UserConnectedAsync(int userId, string connectionId);
    Task UserDisconnectedAsync(int userId, string connectionId);
    Task<IReadOnlyList<int>> GetOnlineFriendsAsync(int userId, IEnumerable<int> allFriendIds);
}

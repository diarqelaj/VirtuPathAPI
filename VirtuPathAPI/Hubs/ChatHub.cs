using Microsoft.AspNetCore.SignalR;


    namespace VirtuPathAPI.Hubs
    {
        public class ChatHub : Hub
        {
            public async Task SendMessageToUser(int senderId, int receiverId, string message)
            {
                await Clients.User(receiverId.ToString())
                             .SendAsync("ReceiveMessage", senderId, message);
            }
        }
    }



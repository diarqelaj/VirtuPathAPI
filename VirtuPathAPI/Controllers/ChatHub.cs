using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using VirtuPathAPI.Controllers; // for IPresenceTracker

namespace VirtuPathAPI.Hubs
{
    public class ChatHub : Hub
    {
        private readonly ChatContext _context;
        private readonly IPresenceTracker _presence;

        public ChatHub(
            ChatContext context,
            IPresenceTracker presenceTracker)
        {
            _context  = context;
            _presence = presenceTracker;
        }

        private int? GetCurrentUserId() =>
            Context.GetHttpContext()?.Session.GetInt32("UserID");

        private Task<bool> AreFriendsAsync(int me, int other) =>
            _context.UserFriends.AnyAsync(f =>
                ((f.FollowerId == me && f.FollowedId == other) ||
                 (f.FollowedId == me && f.FollowerId == other)) &&
                f.IsAccepted);

        private Task<bool> IsRequestAcceptedAsync(int a, int b) =>
            _context.ChatRequests.AnyAsync(r =>
                ((r.SenderId == a && r.ReceiverId == b) ||
                 (r.SenderId == b && r.ReceiverId == a)) &&
                r.IsAccepted);

        private Task<bool> CanChatAsync(int me, int other) =>
            AreFriendsAsync(me, other)
            .ContinueWith(t => t.Result
                || IsRequestAcceptedAsync(me, other).Result
            );

        // ─── 1) SEND CHAT REQUEST ─────────────────────────
        public async Task SendChatRequest(int receiverId, string initialMessage)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            if (await AreFriendsAsync(me.Value, receiverId))
                throw new HubException("You’re already friends—use SendMessage.");

            if (await _context.ChatRequests
                  .AnyAsync(r => r.SenderId == me.Value && r.ReceiverId == receiverId))
                throw new HubException("Request already sent.");

            var req = new ChatRequest
            {
                SenderId   = me.Value,
                ReceiverId = receiverId,
                SentAt     = DateTime.UtcNow,
                IsAccepted = false
            };
            _context.ChatRequests.Add(req);
            await _context.SaveChangesAsync();

            await Clients.User(receiverId.ToString())
                         .SendAsync("ReceiveChatRequest", new {
                             req.Id,
                             req.SenderId,
                             req.ReceiverId,
                             req.SentAt,
                             InitialMessage = initialMessage
                         });
        }

        // ─── 2) ACCEPT CHAT REQUEST ───────────────────────
        public async Task AcceptChatRequest(int senderId)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            var req = await _context.ChatRequests
                .FirstOrDefaultAsync(r =>
                    r.SenderId   == senderId &&
                    r.ReceiverId == me.Value &&
                    !r.IsAccepted);

            if (req == null) throw new HubException("No pending request.");

            req.IsAccepted = true;
            await _context.SaveChangesAsync();

            var dto = new {
                req.Id,
                req.SenderId,
                req.ReceiverId,
                req.SentAt
            };

            await Clients.User(senderId.ToString())
                         .SendAsync("ChatRequestAccepted", dto);
            await Clients.Caller
                         .SendAsync("ChatRequestAccepted", dto);
        }

        // ─── TYPING INDICATOR ────────────────────────────
        public async Task Typing(int toUserId)
        {
            var me = GetCurrentUserId()?.ToString();
            if (me == null) throw new HubException("Not logged in.");
            await Clients.User(toUserId.ToString())
                         .SendAsync("UserTyping", me);
        }

        public async Task StopTyping(int toUserId)
        {
            var me = GetCurrentUserId()?.ToString();
            if (me == null) throw new HubException("Not logged in.");
            await Clients.User(toUserId.ToString())
                         .SendAsync("UserStopTyping", me);
        }

        // ─── PRESENCE ────────────────────────────────────
        public override async Task OnConnectedAsync()
        {
            var userId = GetCurrentUserId();
            if (userId != null)
            {
                await _presence.UserConnectedAsync(userId.Value, Context.ConnectionId);

                var friends = await GetFriendIds(userId.Value);

                // 1) Tell my friends I’m online
                foreach (var f in friends)
                    await Clients.User(f.ToString())
                                .SendAsync("UserOnline", userId.Value);

                // 2) Tell me **which of them** are already online
                var onlineFriends = await _presence.GetOnlineFriendsAsync(userId.Value, friends);
                await Clients.Caller.SendAsync("OnlineFriends", onlineFriends);
            }

            await base.OnConnectedAsync();
        }


        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var userId = GetCurrentUserId();
            if (userId != null)
            {
                // unregister
                await _presence.UserDisconnectedAsync(userId.Value, Context.ConnectionId);

                // notify friends
                var friends = await GetFriendIds(userId.Value);
                foreach (var f in friends)
                    await Clients.User(f.ToString())
                                 .SendAsync("UserOffline", userId.Value);
            }

            await base.OnDisconnectedAsync(ex);
        }

        // helper to fetch friend IDs
        private async Task<IEnumerable<int>> GetFriendIds(int me)
        {
            return await _context.UserFriends
                .Where(f => ((f.FollowerId == me) || (f.FollowedId == me)) && f.IsAccepted)
                .Select(f => f.FollowerId == me ? f.FollowedId : f.FollowerId)
                .ToListAsync();
        }

        // ─── 3) SEND MESSAGE ──────────────────────────────
        public async Task SendMessage(int receiverId, string message, int? replyToMessageId)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            if (!await CanChatAsync(me.Value, receiverId))
                throw new HubException("Not friends or request not accepted.");

            var chat = new ChatMessage
            {
                SenderId         = me.Value,
                ReceiverId       = receiverId,
                Message          = message,
                SentAt           = DateTime.UtcNow,
                ReplyToMessageId = replyToMessageId
            };
            _context.ChatMessages.Add(chat);
            await _context.SaveChangesAsync();

            var dto = new {
                chat.Id,
                chat.SenderId,
                chat.ReceiverId,
                chat.Message,
                chat.ReplyToMessageId,
                chat.SentAt,
                chat.IsEdited
            };

            await Clients.User(me.Value.ToString())
                         .SendAsync("ReceiveMessage", dto);
            await Clients.User(receiverId.ToString())
                         .SendAsync("ReceiveMessage", dto);
        }

        // ─── 4) EDIT MESSAGE ──────────────────────────────
        public async Task EditMessage(int messageId, string newMessage)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            var msg = await _context.ChatMessages.FindAsync(messageId);
            if (msg == null || msg.SenderId != me.Value)
                throw new HubException("Not found or not yours.");

            msg.Message  = newMessage;
            msg.IsEdited = true;
            await _context.SaveChangesAsync();

            var dto = new {
                msg.Id,
                msg.Message,
                msg.IsEdited
            };

            await Clients.User(msg.SenderId.ToString())
                         .SendAsync("MessageEdited", dto);
            await Clients.User(msg.ReceiverId.ToString())
                         .SendAsync("MessageEdited", dto);
        }

        // ─── 5) DELETE FOR SENDER ─────────────────────────
        public async Task DeleteForSender(int messageId)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            var msg = await _context.ChatMessages.FindAsync(messageId);
            if (msg == null || msg.SenderId != me.Value)
                throw new HubException("Not found or not yours.");

            msg.IsDeletedForSender = true;
            await _context.SaveChangesAsync();

            await Clients.User(me.Value.ToString())
                         .SendAsync("MessageDeletedForSender", messageId);
        }

        // ─── 6) DELETE FOR EVERYONE ───────────────────────
        public async Task DeleteForEveryone(int messageId)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            var msg = await _context.ChatMessages.FindAsync(messageId);
            if (msg == null || msg.SenderId != me.Value)
                throw new HubException("Not found or not yours.");

            msg.IsDeletedForSender   =
            msg.IsDeletedForReceiver = true;
            await _context.SaveChangesAsync();

            await Clients.User(msg.SenderId.ToString())
                         .SendAsync("MessageDeletedForEveryone", messageId);
            await Clients.User(msg.ReceiverId.ToString())
                         .SendAsync("MessageDeletedForEveryone", messageId);
        }

        // ─── 7) REACT TO MESSAGE ─────────────────────────
        public async Task ReactToMessage(int messageId, string emoji)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            var msg = await _context.ChatMessages.FindAsync(messageId);
            if (msg == null ||
               (msg.SenderId   != me.Value &&
                msg.ReceiverId != me.Value))
                throw new HubException("Not allowed.");

            var existing = await _context.MessageReactions
                .FirstOrDefaultAsync(r =>
                     r.MessageId == messageId &&
                     r.UserId    == me.Value);

            if (existing != null) existing.Emoji = emoji;
            else
                _context.MessageReactions.Add(new MessageReaction {
                    MessageId = messageId,
                    UserId    = me.Value,
                    Emoji     = emoji
                });

            await _context.SaveChangesAsync();

            var reactionDto = new {
                MessageId = messageId,
                UserId    = me.Value,
                Emoji     = emoji
            };

            await Clients.User(msg.SenderId.ToString())
                         .SendAsync("MessageReacted", reactionDto);
            await Clients.User(msg.ReceiverId.ToString())
                         .SendAsync("MessageReacted", reactionDto);
        }

        // ─── 8) REMOVE REACTION ──────────────────────────
        public async Task RemoveReaction(int messageId)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            var reaction = await _context.MessageReactions
                .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == me.Value);
            if (reaction == null) throw new HubException("Not found.");

            var chat = await _context.ChatMessages
                .Where(m => m.Id == messageId)
                .Select(m => new { m.SenderId, m.ReceiverId })
                .FirstOrDefaultAsync();
            if (chat == null) throw new HubException("Message not found.");

            _context.MessageReactions.Remove(reaction);
            await _context.SaveChangesAsync();

            var dto = new { MessageId = messageId, UserId = me.Value };
            await Clients.User(chat.SenderId.ToString())
                         .SendAsync("MessageReactionRemoved", dto);
            await Clients.User(chat.ReceiverId.ToString())
                         .SendAsync("MessageReactionRemoved", dto);
        }
    }
}

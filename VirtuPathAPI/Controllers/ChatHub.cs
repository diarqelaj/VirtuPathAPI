using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using VirtuPathAPI.Controllers;
using System.Collections.Concurrent;

namespace VirtuPathAPI.Hubs
{
    public class ChatHub : Hub
    {
        private readonly ChatContext _context;
        private readonly IPresenceTracker _presence;
        private readonly RSA _rsaPrivate;  // ← server’s RSA private key

        public ChatHub(
            ChatContext context,
            IPresenceTracker presenceTracker,
            RSA rsaPrivate      // ← injected via DI
        )
        {
            _context    = context;
            _presence   = presenceTracker;
            _rsaPrivate = rsaPrivate;
        }

        // ─── Typing‐throttle setup ────────────────────────
        private static readonly TimeSpan TypingThrottle = TimeSpan.FromMilliseconds(500);
        private const int TypingCachePruneThreshold = 1000;
        private readonly ConcurrentDictionary<(int from, int to), DateTime> _lastTyping 
            = new();

        private int? GetCurrentUserId() =>
            Context.GetHttpContext()?.Session.GetInt32("UserID");

        // ─── FRIENDSHIP CHECKS (read‐only) ───────────────
        private Task<bool> AreFriendsAsync(int me, int other) =>
            _context.UserFriends
                    .AsNoTracking()
                    .AnyAsync(f =>
                        ((f.FollowerId == me && f.FollowedId == other) ||
                         (f.FollowedId == me && f.FollowerId == other)) &&
                        f.IsAccepted);

        private Task<bool> IsRequestAcceptedAsync(int a, int b) =>
            _context.ChatRequests
                    .AsNoTracking()
                    .AnyAsync(r =>
                        ((r.SenderId == a && r.ReceiverId == b) ||
                         (r.SenderId == b && r.ReceiverId == a)) &&
                        r.IsAccepted);

        private async Task<bool> CanChatAsync(int me, int other)
        {
            if (await AreFriendsAsync(me, other)) return true;
            return await IsRequestAcceptedAsync(me, other);
        }

        // ─── 1) SEND CHAT REQUEST ─────────────────────────
        public async Task SendChatRequest(int receiverId, string initialMessage)
        {
            var me = GetCurrentUserId();
            if (me is null) throw new HubException("Not logged in.");

            if (await AreFriendsAsync(me.Value, receiverId))
                throw new HubException("You’re already friends—use SendMessage.");

            if (await _context.ChatRequests
                  .AsNoTracking()
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
                         .SendAsync("ReceiveChatRequest", new
                         {
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
            await Clients.Caller.SendAsync("ChatRequestAccepted", dto);
        }

        // ─── 3) TYPING INDICATOR ──────────────────────────
        public Task Typing(int toUserId)
        {
            var me = GetCurrentUserId();
            if (me == null) throw new HubException("Not logged in.");

            // prune stale entries if our cache is too big
            if (_lastTyping.Count > TypingCachePruneThreshold)
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
                foreach (var kv in _lastTyping.Where(kv => kv.Value < cutoff))
                    _lastTyping.TryRemove(kv.Key, out _);
            }

            var key = (from: me.Value, to: toUserId);
            var now = DateTime.UtcNow;

            // throttle
            if (_lastTyping.TryGetValue(key, out var last) &&
                now - last < TypingThrottle)
                return Task.CompletedTask;

            _lastTyping[key] = now;
            return Clients.User(toUserId.ToString())
                          .SendAsync("UserTyping", me.Value);
        }

        public Task StopTyping(int toUserId)
        {
            var me = GetCurrentUserId();
            if (me == null) throw new HubException("Not logged in.");
            return Clients.User(toUserId.ToString())
                          .SendAsync("UserStopTyping", me.Value);
        }

        // ─── 4) PRESENCE ──────────────────────────────────
        public override async Task OnConnectedAsync()
        {
            var userId = GetCurrentUserId();
            if (userId != null)
            {
                await _presence.UserConnectedAsync(userId.Value, Context.ConnectionId);

                var friends = await GetFriendIds(userId.Value);

                // 1) Tell my friends I’m online (in parallel)
                var onlineTasks = friends
                    .Select(f => Clients.User(f.ToString())
                                        .SendAsync("UserOnline", userId.Value));
                await Task.WhenAll(onlineTasks);

                // 2) Tell me which of them are online
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
                await _presence.UserDisconnectedAsync(userId.Value, Context.ConnectionId);

                var friends = await GetFriendIds(userId.Value);

                // broadcast offline in parallel
                var offlineTasks = friends
                    .Select(f => Clients.User(f.ToString())
                                        .SendAsync("UserOffline", userId.Value));
                await Task.WhenAll(offlineTasks);
            }

            await base.OnDisconnectedAsync(ex);
        }

        // ─── helper to fetch friend IDs ──────────────────
        private async Task<IEnumerable<int>> GetFriendIds(int me)
        {
            return await _context.UserFriends
                .AsNoTracking()
                .Where(f => ((f.FollowerId == me) || (f.FollowedId == me)) && f.IsAccepted)
                .Select(f => f.FollowerId == me ? f.FollowedId : f.FollowerId)
                .ToListAsync();
        }

        // ─── 5) SEND MESSAGE (store blob + broadcast) ────
        // -----------------------------------------------------------------------------
        public async Task SendMessage(
            int    receiverId,
            string wrappedKeyForSenderB64,
            string wrappedKeyForReceiverB64,
            string ivB64,
            string ciphertextB64,
            string tagB64,
            int?   replyToMessageId)
        {
            var me = GetCurrentUserId();
            if (me == null) throw new HubException("Not logged in.");
            if (!await CanChatAsync(me.Value, receiverId))
                throw new HubException("Not friends or request not accepted.");

            var chat = new ChatMessage
            {
                SenderId              = me.Value,
                ReceiverId            = receiverId,
                WrappedKeyForSender   = wrappedKeyForSenderB64,
                WrappedKeyForReceiver = wrappedKeyForReceiverB64,
                Iv                    = ivB64,
                Tag                   = tagB64,
                Message               = ciphertextB64,
                SentAt                = DateTime.UtcNow,
                ReplyToMessageId      = replyToMessageId
            };

            _context.ChatMessages.Add(chat);
            await _context.SaveChangesAsync();

            /* full DTO — now includes the two wrapped keys 🚀 */
            var dto = new
            {
                id                   = chat.Id,
                senderId             = chat.SenderId,
                receiverId           = chat.ReceiverId,
                wrappedKeyForSenderB64   = chat.WrappedKeyForSender,
                wrappedKeyForReceiverB64 = chat.WrappedKeyForReceiver,
                ivB64                = chat.Iv,
                ciphertextB64        = chat.Message,
                tagB64               = chat.Tag,
                sentAt               = chat.SentAt,
                replyToMessageId     = chat.ReplyToMessageId
            };

            // 1) tell *me* it’s delivered
            _ = Clients.Caller.SendAsync("MessageDelivered", chat.Id);

            // 2) broadcast encrypted copy to both parties
            await Task.WhenAll(
                Clients.User(receiverId.ToString()).SendAsync("ReceiveEncryptedMessage", dto),
                Clients.Caller.SendAsync("ReceiveEncryptedMessage", dto)
            );
        }

        // ─── 6) EDIT MESSAGE ─────────────────────────────
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

            var dto = new { msg.Id, msg.Message, msg.IsEdited };

            await Clients.User(msg.SenderId.ToString())
                         .SendAsync("MessageEdited", dto);
            await Clients.User(msg.ReceiverId.ToString())
                         .SendAsync("MessageEdited", dto);
        }

        // ─── 7) DELETE FOR SENDER ────────────────────────
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

        // ─── 8) DELETE FOR EVERYONE ──────────────────────
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

        // ─── 9) REACT TO MESSAGE ─────────────────────────
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
            else _context.MessageReactions.Add(new MessageReaction {
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

        // ─── 10) REMOVE REACTION ─────────────────────────
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

        // ─── 11) BULK READ‐ACK ───────────────────────────
        public async Task AcknowledgeReadBulk(int[] messageIds)
        {
            // me is now an int, not int?
            var me = GetCurrentUserId()
                    ?? throw new HubException("Not logged in.");

            // use me directly
            var toAck = await _context.ChatMessages
                        .Where(m => messageIds.Contains(m.Id) 
                                    && m.ReceiverId == me)
                        .ToListAsync();

            var now = DateTime.UtcNow;
            toAck.ForEach(m => { m.IsRead = true; m.ReadAt = now; });
            await _context.SaveChangesAsync();

            var tasks = toAck
                .Select(m => Clients.User(m.SenderId.ToString())
                                .SendAsync("MessageRead", m.Id));
            await Task.WhenAll(tasks);
        }

        // ─── 12) SINGLE READ‐ACK ─────────────────────────
        public async Task AcknowledgeRead(int messageId)
        {
            var me = GetCurrentUserId();
            if (me == null) throw new HubException("Not logged in.");

            var msg = await _context.ChatMessages.FindAsync(messageId);
            if (msg == null || msg.ReceiverId != me.Value)
                throw new HubException("Not found or not yours.");

            msg.IsRead  = true;
            msg.ReadAt  = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await Clients.User(msg.SenderId.ToString())
                         .SendAsync("MessageRead", messageId);
        }

        // ─── 13) DELIVERED‐ACK ───────────────────────────
        public async Task AcknowledgeDelivered(int messageId)
        {
            var me = GetCurrentUserId();
            if (me == null) throw new HubException("Not logged in.");

            var msg = await _context.ChatMessages.FindAsync(messageId);
            if (msg == null || msg.ReceiverId != me.Value)
                throw new HubException("Not found or not yours.");

            msg.IsDelivered   = true;
            msg.DeliveredAt   = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await Clients.User(msg.SenderId.ToString())
                         .SendAsync("MessageDelivered", messageId);
        }
    }
}

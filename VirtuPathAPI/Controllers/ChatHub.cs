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
using Microsoft.Data.SqlClient;

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
        /// <summary>Runs a stored-procedure or raw SQL.</summary>
         private Task<int> ExecAsync(string sql, params SqlParameter[] p) =>
             _context.Database.ExecuteSqlRawAsync(sql, p);

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
             string dhPubB64,
             int    pn,
             int    n,
             string ivB64,
             string ciphertextB64,
             string tagB64,
             int?   replyToMessageId,
             string? reactionEmoji = null)  
        {
            var me = GetCurrentUserId();
            if (me == null) throw new HubException("Not logged in.");
            if (!await CanChatAsync(me.Value, receiverId))
                throw new HubException("Not friends or request not accepted.");

            var msg = new ChatMessage
            {
                SenderId   = me.Value,
                ReceiverId = receiverId,

                // ratchet header
                DhPubB64 = dhPubB64.Trim(),
                PN       = pn,
                N        = n,

                // encrypted blob
                Iv      = ivB64,
                Message = ciphertextB64,
                Tag     = tagB64,

                SentAt           = DateTimeOffset.UtcNow,
                ReplyToMessageId = replyToMessageId
            };

            _context.ChatMessages.Add(msg);

            if (!string.IsNullOrWhiteSpace(reactionEmoji))
            {
                _context.MessageReactions.Add(new MessageReaction
                {
                    MessageId = msg.Id,    // EF fills after SaveChanges
                    UserId    = me.Value,
                    Emoji     = reactionEmoji!
                });
            }

            await _context.SaveChangesAsync(); 

            /* full DTO — now includes the two wrapped keys 🚀 */
            var dto = new
            {
                id         = msg.Id,
                senderId   = msg.SenderId,
                receiverId = msg.ReceiverId,

                // ratchet header
                dhPubB64 = msg.DhPubB64,
                pn       = msg.PN,
                n        = msg.N,

                // ciphertext
                ivB64         = msg.Iv,
                ciphertextB64 = msg.Message,
                tagB64        = msg.Tag,
                sentAt        = msg.SentAt,
                replyToMessageId = msg.ReplyToMessageId
            };

            // 1) optimistic “delivered” back to myself
            _ = Clients.Caller.SendAsync("MessageDelivered", msg.Id);

            // 2) encrypted payload to both sides
            await Task.WhenAll(
                Clients.User(receiverId.ToString()).SendAsync("ReceiveEncryptedMessage", dto),
                Clients.Caller.SendAsync("ReceiveEncryptedMessage", dto)
            );

            // 3) if there was an inline reaction → push that too
            if (!string.IsNullOrWhiteSpace(reactionEmoji))
            {
                var reactDto = new { messageId = msg.Id, userId = me.Value, emoji = reactionEmoji };
                await Task.WhenAll(
                    Clients.User(receiverId.ToString()).SendAsync("MessageReacted", reactDto),
                    Clients.Caller.SendAsync("MessageReacted", reactDto)
                );
            }
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
            if (messageIds == null || messageIds.Length == 0) return;

             // 1) keep only messages where *I* am the receiver
   var mine = await _context.ChatMessages
                            .Where(m => messageIds.Contains(m.Id) &&
                                        m.ReceiverId == me)
                            .Select(m => m.Id)
                            .ToListAsync();
   if (mine.Count == 0) return;

            var csv = string.Join(",", mine);               // `mine` is the validated list

            await ExecAsync(
                "EXEC Chat.MarkReadBulk @IdsCsv, @ReceiverId",
                new SqlParameter("@IdsCsv",     csv),
                new SqlParameter("@ReceiverId", me)          // 👈 new arg
            );
            var meta = await _context.ChatMessages
                                    .Where(m => messageIds.Contains(m.Id))                          .Select(m => new { m.Id, m.SenderId, m.ReadAt })
                                    .ToListAsync();

            var bySender = meta.GroupBy(m => m.SenderId);
            foreach (var g in bySender)
            {
                var payload = g.Select(r => new { messageId = r.Id, readAt = r.ReadAt });
                await Clients.User(g.Key.ToString()).SendAsync("MessagesRead", payload);
            }

            await Clients.Caller.SendAsync("MessagesRead",
                meta.Select(r => new { messageId = r.Id, readAt = r.ReadAt }));
        }
// ───── 13)  DELIVERED-ACK ───────────────────────────────
            public async Task AcknowledgeDelivered(int messageId)
            {
                var me = GetCurrentUserId()
                        ?? throw new HubException("Not logged in.");
                await ExecAsync("EXEC Chat.MarkDelivered @Id",
                 new SqlParameter("@Id", messageId));

                    var info = await _context.ChatMessages
                                            .Where(m => m.Id == messageId)
                                            .Select(m => new { m.DeliveredAt, m.SenderId, m.ReceiverId })
                                            .SingleAsync();

                    var peerId  = (me == info.SenderId) ? info.ReceiverId : info.SenderId;
                    var payload = new { messageId, deliveredAt = info.DeliveredAt };

                    await Clients.User(me.ToString()).SendAsync("MessageDelivered", payload);
                    await Clients.User(peerId.ToString()).SendAsync("MessageDelivered", payload);
                                    
                        }

            // ───── 14)  READ-ACK ────────────────────────────────────
            public async Task AcknowledgeRead(int messageId)
            {
                var me = GetCurrentUserId()
                        ?? throw new HubException("Not logged in.");

                var msg = await _context.ChatMessages.FindAsync(messageId);
                if (msg == null || msg.ReceiverId != me)
                    throw new HubException("Not found or not yours.");

                if (!msg.IsRead)
                {
                    msg.IsRead = true;
                    msg.ReadAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }

                await Clients.User(msg.SenderId.ToString())
                            .SendAsync("MessageRead", new { messageId, msg.ReadAt });
                await Clients.User(msg.ReceiverId.ToString())
                            .SendAsync("MessageRead", new { messageId, msg.ReadAt });
            }

    }
}
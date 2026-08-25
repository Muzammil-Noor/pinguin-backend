using Microsoft.AspNetCore.SignalR;
using Pinguin.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Pinguin.Hubs
{
    public class ChatHub : Hub
    {
        private readonly UserManager _userManager;
        private readonly ChatroomManager _chatroomManager;
        private readonly StudyRoomManager _studyRoomManager;
        private readonly StudyRoomAiMemory _aiMemory;
        private readonly StudyRoomRateLimiter _rateLimiter;
        private readonly ILlmService _llmService;
        private readonly BlockManager _blockManager;
        private readonly WhiteboardManager _whiteboard;
        private readonly WhiteboardRateLimiter _whiteboardLimiter;
        private readonly VoiceChannelManager _voice;
        private readonly VoiceRateLimiter _voiceLimiter;

        private static readonly ConcurrentDictionary<string, string> _publicKeys = new();

        public ChatHub(
            UserManager userManager, 
            ChatroomManager chatroomManager,
            StudyRoomManager studyRoomManager,
            StudyRoomAiMemory aiMemory,
            StudyRoomRateLimiter rateLimiter,
            ILlmService llmService,
            BlockManager blockManager,
            WhiteboardManager whiteboard,
            WhiteboardRateLimiter whiteboardLimiter,
            VoiceChannelManager voice,
            VoiceRateLimiter voiceLimiter)
        {
            _userManager = userManager;
            _chatroomManager = chatroomManager;
            _studyRoomManager = studyRoomManager;
            _aiMemory = aiMemory;
            _rateLimiter = rateLimiter;
            _llmService = llmService;
            _blockManager = blockManager;
            _whiteboard = whiteboard;
            _whiteboardLimiter = whiteboardLimiter;
            _voice = voice;
            _voiceLimiter = voiceLimiter;
        }

        // =========================
        // BLOCK FILTERING
        // =========================

        private IReadOnlyList<string>? GetBlockedRecipients(string sender)
        {
            List<string>? excluded = null;

            foreach (var blocker in _blockManager.GetBlockersOf(sender))
            {
                var connectionId = _userManager.GetConnectionId(blocker);
                if (connectionId != null) (excluded ??= new List<string>()).Add(connectionId);
            }

            return excluded;
        }

        private IClientProxy AllExcludingBlockers(string sender)
        {
            var excluded = GetBlockedRecipients(sender);
            return excluded == null ? Clients.All : Clients.AllExcept(excluded);
        }

        private IClientProxy GroupExcludingBlockers(string groupName, string sender)
        {
            var excluded = GetBlockedRecipients(sender);
            return excluded == null ? Clients.Group(groupName) : Clients.GroupExcept(groupName, excluded);
        }

        public async Task<bool> JoinChat(string username)
        {
            var success = _userManager.TryAddUser(Context.ConnectionId, username);
            if (!success) return false;

            await Clients.Others.SendAsync("UserJoined", username);

            // Send all existing public keys to the new user
            foreach (var kvp in _publicKeys) await Clients.Caller.SendAsync("UserPublicKey", kvp.Key, kvp.Value);

            return true;
        }

        public Task RegisterPublicKey(string username, string publicKeyPem)
        {
            _publicKeys[username] = publicKeyPem;

            // Broadcast this user's public key to others
            return Clients.Others.SendAsync("UserPublicKey", username, publicKeyPem);
        }

        public IEnumerable<string> GetOnlineUsers()
        {
            return _userManager.GetAllUsers();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var username = _userManager.RemoveUser(Context.ConnectionId);
            if (username != null)
            {
                _publicKeys.TryRemove(username, out _);

                // The username is freed immediately, so its blocks must die with it --
                // whoever claims the name next must not inherit them.
                _blockManager.RemoveUser(username);
                _whiteboardLimiter.RemoveConnection(Context.ConnectionId);
                _voiceLimiter.RemoveConnection(Context.ConnectionId);

                foreach (var voiceRoomId in _voice.RemoveFromAll(username))
                {
                    await Clients.Group(VoiceGroup(voiceRoomId))
                        .SendAsync("VoiceParticipantLeft", voiceRoomId, username);
                }

                await Clients.All.SendAsync("UserLeft", username);

                // Chatrooms cleanup
                var rooms = _chatroomManager.GetAllRooms().ToList();
                foreach (var room in rooms)
                {
                    if (room.Members.Contains(username))
                    {
                        var (removedRoom, wasOwner, deleted) = _chatroomManager.RemoveMember(room.Id, username);
                        if (removedRoom != null)
                        {
                            if (deleted)
                            {
                                _whiteboard.RemoveBoard(room.Id);
                                _voice.RemoveChannel(room.Id);
                                await Clients.All.SendAsync("RoomDeleted", room.Id);
                            }
                            else
                            {
                                _whiteboard.RemoveUser(room.Id, username);
                                string? newOwner = wasOwner ? removedRoom.Owner : null;
                                await Clients.Group(room.Id).SendAsync("RoomMemberLeft", room.Id, username, newOwner);
                            }
                        }
                    }
                }

                // Study rooms cleanup
                var studyRooms = _studyRoomManager.GetRoomsForUser(username).ToList();
                foreach (var room in studyRooms)
                {
                    var (removedRoom, wasOwner, deleted) = _studyRoomManager.RemoveMember(room.Id, username);
                    if (removedRoom != null)
                    {
                        if (deleted)
                        {
                            await Clients.Group($"study_{room.Id}").SendAsync("StudyRoomDeleted", room.Id);
                            _aiMemory.ClearRoom(room.Id);
                            _rateLimiter.ClearRoom(room.Id);
                        }
                        else
                        {
                            string? newOwner = wasOwner ? removedRoom.Owner : null;
                            await Clients.Group($"study_{room.Id}").SendAsync("StudyRoomMemberLeft", room.Id, username, newOwner);
                        }
                    }
                }

                // Pending study room invites cleanup
                var affectedInvites = _studyRoomManager.RemoveInvitesInvolving(username);
                foreach (var invite in affectedInvites)
                {
                    await Clients.Group($"study_invite_{invite.Id}").SendAsync("StudyRoomInviteCancelled", invite.Id, username);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(object message)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            await AllExcludingBlockers(username).SendAsync("MessageReceived", username, message);
        }

        public async Task SendFile(string fileName, string fileData, string? toUser, string? caption = null)
        {
            var senderUsername = _userManager.GetUsername(Context.ConnectionId);
            if (senderUsername == null) return;

            if (string.IsNullOrEmpty(toUser))
            {
                // Global file
                await AllExcludingBlockers(senderUsername).SendAsync("FileReceived", senderUsername, fileName, fileData, false, null, caption);
            }
            else
            {
                // Private file. A block in either direction suppresses delivery.
                var suppressed = _blockManager.IsBlockedEitherWay(senderUsername, toUser);
                var targetConnectionId = suppressed ? null : _userManager.GetConnectionId(toUser);

                if (targetConnectionId != null)
                {
                    // Send to recipient
                    await Clients.Client(targetConnectionId).SendAsync("FileReceived", senderUsername, fileName, fileData, true, null, caption);
                }

                // Send back to caller so they see their own file -- echoed even when delivery was
                // suppressed, so a blocked sender cannot detect the block.
                await Clients.Caller.SendAsync("FileReceived", senderUsername, fileName, fileData, true, toUser, caption);
            }
        }

        // Pure encrypted relay
        public async Task SendPrivateMessage(string toUsername, object payload)
        {
            var senderUsername = _userManager.GetUsername(Context.ConnectionId);
            if (senderUsername == null) return;

            // A block in either direction makes the DM read-only. Delivery is dropped, but the
            // sender's own echo still fires so a blocked user cannot detect the block (PRD 7.1).
            if (!_blockManager.IsBlockedEitherWay(senderUsername, toUsername))
            {
                var targetConnectionId = _userManager.GetConnectionId(toUsername);
                if (targetConnectionId != null)
                {
                    await Clients.Client(targetConnectionId)
                        .SendAsync("PrivateMessageReceived", senderUsername, payload);
                }
            }

            await Clients.Caller
                .SendAsync("PrivateMessageSent", toUsername, payload);
        }

        // =========================
        // BLOCKING
        // =========================

        public async Task<bool> BlockUser(string targetUsername)
        {
            var requester = _userManager.GetUsername(Context.ConnectionId);
            if (requester == null) return false;
            if (!_blockManager.Block(requester, targetUsername)) return false;

            // Only the blocker is told. Blocked users are never notified (PRD 7.1).
            await Clients.Caller.SendAsync("UserBlocked", targetUsername);
            return true;
        }

        public async Task<bool> UnblockUser(string targetUsername)
        {
            var requester = _userManager.GetUsername(Context.ConnectionId);
            if (requester == null) return false;
            if (!_blockManager.Unblock(requester, targetUsername)) return false;

            await Clients.Caller.SendAsync("UserUnblocked", targetUsername);
            return true;
        }

        public IEnumerable<string> GetBlockedUsers()
        {
            var requester = _userManager.GetUsername(Context.ConnectionId);
            if (requester == null) return Enumerable.Empty<string>();
            return _blockManager.GetBlockedBy(requester);
        }

        // =========================
        // TYPING INDICATORS
        // =========================

        public Task StartTyping(string scope) => BroadcastTyping(scope, true);

        public Task StopTyping(string scope) => BroadcastTyping(scope, false);

        private async Task BroadcastTyping(string scope, bool isTyping)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null || string.IsNullOrEmpty(scope)) return;

            if (scope == "global")
            {
                await AllExcludingBlockers(username).SendAsync("TypingIndicator", scope, username, isTyping);
                return;
            }

            var room = _chatroomManager.GetRoom(scope);
            if (room != null)
            {
                if (!room.Members.Contains(username)) return;
                await GroupExcludingBlockers(scope, username).SendAsync("TypingIndicator", scope, username, isTyping);
                return;
            }

            var studyRoom = _studyRoomManager.GetRoom(scope);
            if (studyRoom != null)
            {
                if (!studyRoom.Members.Contains(username)) return;
                await GroupExcludingBlockers($"study_{scope}", username).SendAsync("TypingIndicator", scope, username, isTyping);
                return;
            }

            // Anything else is a DM peer's username.
            if (_blockManager.IsBlockedEitherWay(username, scope)) return;

            var targetConnectionId = _userManager.GetConnectionId(scope);
            if (targetConnectionId == null) return;

            // The peer files this DM under the sender's name, not their own.
            await Clients.Client(targetConnectionId).SendAsync("TypingIndicator", username, username, isTyping);
        }

        // =========================
        // CHATROOMS
        // =========================

        public async Task<object?> CreateRoom(string name)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return null;

            var room = _chatroomManager.CreateRoom(name, username);
            await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);
            
            await Clients.Caller.SendAsync("RoomCreated", room);
            return room;
        }

        public async Task<bool> DeleteRoom(string roomId)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return false;

            if (_chatroomManager.DeleteRoom(roomId, username))
            {
                _whiteboard.RemoveBoard(roomId);
                _voice.RemoveChannel(roomId);
                await Clients.Group(roomId).SendAsync("RoomDeleted", roomId);
                return true;
            }
            return false;
        }

        public async Task<bool> RenameRoom(string roomId, string newName)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return false;

            if (_chatroomManager.RenameRoom(roomId, newName, username))
            {
                await Clients.Group(roomId).SendAsync("RoomRenamed", roomId, newName);
                return true;
            }
            return false;
        }

        public async Task<bool> InviteToRoom(string roomId, string targetUsername)
        {
            var requester = _userManager.GetUsername(Context.ConnectionId);
            if (requester == null) return false;

            var room = _chatroomManager.GetRoom(roomId);
            if (room == null || room.Owner != requester) return false;

            if (_chatroomManager.TryAddMember(roomId, targetUsername))
            {
                var targetConnId = _userManager.GetConnectionId(targetUsername);
                if (targetConnId != null)
                {
                    await Groups.AddToGroupAsync(targetConnId, roomId);
                    await Clients.Client(targetConnId).SendAsync("RoomInvited", room);
                }
                await Clients.Group(roomId).SendAsync("RoomMemberJoined", roomId, targetUsername);
                return true;
            }
            return false;
        }

        public async Task<bool> KickFromRoom(string roomId, string targetUsername)
        {
            var requester = _userManager.GetUsername(Context.ConnectionId);
            if (requester == null) return false;

            if (_chatroomManager.KickMember(roomId, targetUsername, requester))
            {
                var targetConnId = _userManager.GetConnectionId(targetUsername);
                if (targetConnId != null)
                {
                    await Clients.Client(targetConnId).SendAsync("KickedFromRoom", roomId);
                    await Groups.RemoveFromGroupAsync(targetConnId, roomId);
                }
                await Clients.Group(roomId).SendAsync("RoomMemberLeft", roomId, targetUsername, null);
                return true;
            }
            return false;
        }

        public async Task LeaveRoom(string roomId)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var (room, wasOwner, deleted) = _chatroomManager.RemoveMember(roomId, username);
            if (room != null)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
                
                if (deleted)
                {
                    _whiteboard.RemoveBoard(roomId);
                    _voice.RemoveChannel(roomId);
                    await Clients.All.SendAsync("RoomDeleted", roomId);
                }
                else
                {
                    _whiteboard.RemoveUser(roomId, username);
                    string? newOwner = wasOwner ? room.Owner : null;
                    await Clients.Group(roomId).SendAsync("RoomMemberLeft", roomId, username, newOwner);
                }
            }
        }

        public async Task SendRoomMessage(string roomId, object payload)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var room = _chatroomManager.GetRoom(roomId);
            if (room == null || !room.Members.Contains(username)) return;

            await GroupExcludingBlockers(roomId, username).SendAsync("RoomMessageReceived", roomId, username, payload);
        }

        public async Task SendRoomFile(string roomId, string fileName, string fileData, string? caption = null)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var room = _chatroomManager.GetRoom(roomId);
            if (room == null || !room.Members.Contains(username)) return;

            await GroupExcludingBlockers(roomId, username).SendAsync("RoomFileReceived", roomId, username, fileName, fileData, caption);
        }

        public IEnumerable<object> GetRooms()
        {
            return _chatroomManager.GetAllRooms();
        }

        // =========================
        // WHITEBOARD
        // =========================
        /*
        
            Whiteboard ops are deliberately NOT filtered by block. A canvas is shared state, not
            a message: hiding one user's strokes from another would leave the two clients with
            different pixels, and a later flood fill would then spread differently for each of
            them.

        */

        private (string? username, Chatroom? room) ResolveRoomMembership(string roomId)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return (null, null);

            var room = _chatroomManager.GetRoom(roomId);
            if (room == null || !room.Members.Contains(username)) return (username, null);

            return (username, room);
        }

        public async Task SendWhiteboardAction(string roomId, JsonElement op)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return;

            if (!_whiteboardLimiter.TryCommit(Context.ConnectionId))
            {
                await Clients.Caller.SendAsync("RateLimitExceeded", "whiteboard",
                    _whiteboardLimiter.GetCommitResetSeconds(Context.ConnectionId));
                return;
            }

            var result = _whiteboard.TryAddOp(roomId, username, op, out var committed);

            if (result != WhiteboardManager.AddResult.Ok || committed == null)
            {
                await Clients.Caller.SendAsync("WhiteboardRejected", roomId, result.ToString());
                return;
            }

            await Clients.Group(roomId).SendAsync("WhiteboardEvent", roomId, new
            {
                id = committed.Id,
                seq = committed.Seq,
                author = committed.Author,
                payload = committed.Payload,
                hidden = false
            });
        }

        public async Task StreamWhiteboardStroke(string roomId, string strokeId, JsonElement meta, float[] points)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return;

            // Silent drop: a throttled preview frame is not worth interrupting the artist over,
            // and the commit still lands.
            if (!_whiteboardLimiter.TryLiveBatch(Context.ConnectionId)) return;

            await Clients.OthersInGroup(roomId)
                .SendAsync("WhiteboardLive", roomId, strokeId, username, meta, points);
        }

        public async Task CancelWhiteboardStroke(string roomId, string strokeId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return;

            await Clients.OthersInGroup(roomId).SendAsync("WhiteboardLiveEnd", roomId, strokeId);
        }

        public async Task UndoWhiteboard(string roomId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return;

            // Only ever finds ops authored by this user (PRD 8.4: undo is per-user).
            var opId = _whiteboard.Undo(roomId, username);
            if (opId == null) return;

            await Clients.Group(roomId).SendAsync("WhiteboardUndo", roomId, opId);
        }

        public async Task RedoWhiteboard(string roomId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return;

            var opId = _whiteboard.Redo(roomId, username);
            if (opId == null) return;

            await Clients.Group(roomId).SendAsync("WhiteboardRedo", roomId, opId);
        }

        public async Task ClearWhiteboard(string roomId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return;

            _whiteboard.Clear(roomId);
            await Clients.Group(roomId).SendAsync("WhiteboardCleared", roomId, username);
        }

        public object GetWhiteboardState(string roomId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return new { ops = Array.Empty<object>() };

            var ops = _whiteboard.GetState(roomId).Select(o => new
            {
                id = o.Id,
                seq = o.Seq,
                author = o.Author,
                payload = o.Payload,
                hidden = o.Hidden
            });

            return new { ops };
        }

        // =========================
        // VOICE
        // =========================
        //
        // Signalling only -- no audio touches the server. Peers connect directly, so a block
        // is enforced by never introducing the two clients to each other: the blocked user is
        // omitted from the participant list, from join announcements, and from the relay, so
        // no peer connection between them is ever attempted.

        private bool VoiceVisible(string a, string b) => !_blockManager.IsBlockedEitherWay(a, b);

        public async Task<object> JoinVoice(string roomId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return new { ok = false, reason = "notAMember" };

            var result = _voice.TryJoin(roomId, username, out var existing, out var speaking);

            if (result == VoiceChannelManager.JoinResult.Full)
            {
                return new { ok = false, reason = "full", capacity = VoiceChannelManager.MaxParticipants };
            }
            if (result == VoiceChannelManager.JoinResult.AlreadyJoined)
            {
                return new { ok = false, reason = "alreadyJoined" };
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, VoiceGroup(roomId));

            foreach (var participant in existing)
            {
                if (!VoiceVisible(username, participant)) continue;

                var connId = _userManager.GetConnectionId(participant);
                if (connId != null)
                {
                    await Clients.Client(connId).SendAsync("VoiceParticipantJoined", roomId, username);
                }
            }

            // The newcomer is the one who offers, which is what keeps two peers from
            // negotiating against each other at the same time.
            return new
            {
                ok = true,
                participants = existing.Where(p => VoiceVisible(username, p)).ToList(),
                speaking = speaking.Where(p => VoiceVisible(username, p)).ToList(),
                capacity = VoiceChannelManager.MaxParticipants
            };
        }

        public async Task LeaveVoice(string roomId)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            if (!_voice.Leave(roomId, username)) return;

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, VoiceGroup(roomId));
            await Clients.Group(VoiceGroup(roomId)).SendAsync("VoiceParticipantLeft", roomId, username);
        }

        public async Task SendVoiceSignal(string roomId, string targetUsername, object signal)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            if (!_voice.IsInChannel(roomId, username) || !_voice.IsInChannel(roomId, targetUsername)) return;
            if (!VoiceVisible(username, targetUsername)) return;

            if (!_voiceLimiter.TrySignal(Context.ConnectionId))
            {
                await Clients.Caller.SendAsync("RateLimitExceeded", "voice", 0);
                return;
            }

            var targetConnId = _userManager.GetConnectionId(targetUsername);
            if (targetConnId == null) return;

            await Clients.Client(targetConnId).SendAsync("VoiceSignal", roomId, username, signal);
        }

        public Task PushToTalkStart(string roomId) => BroadcastSpeaking(roomId, true);

        public Task PushToTalkStop(string roomId) => BroadcastSpeaking(roomId, false);

        private async Task BroadcastSpeaking(string roomId, bool isSpeaking)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            if (!_voice.SetSpeaking(roomId, username, isSpeaking)) return;
            if (!_voiceLimiter.TryPushToTalk(Context.ConnectionId)) return;

            var excluded = GetBlockedRecipients(username);
            var target = excluded == null
                ? Clients.Group(VoiceGroup(roomId))
                : Clients.GroupExcept(VoiceGroup(roomId), excluded);

            await target.SendAsync("SpeakingIndicator", roomId, username, isSpeaking);
        }

        public object GetVoiceState(string roomId)
        {
            var (username, room) = ResolveRoomMembership(roomId);
            if (username == null || room == null) return new { participants = Array.Empty<string>() };

            return new
            {
                participants = _voice.GetParticipants(roomId).Where(p => VoiceVisible(username, p)).ToList(),
                capacity = VoiceChannelManager.MaxParticipants
            };
        }

        private static string VoiceGroup(string roomId) => $"voice_{roomId}";

        // =========================
        // STUDY ROOMS
        // =========================

        public async Task<string?> CreateStudyRoom(List<string> invitedUsernames)
        {
            var creator = _userManager.GetUsername(Context.ConnectionId);
            if (creator == null || invitedUsernames == null || invitedUsernames.Count == 0) return null;

            // Self must be included in members list but invitedUsernames should not contain self for the invite broadcast
            var allMembers = invitedUsernames.Distinct().ToList();
            allMembers.Add(creator);

            if (allMembers.Count < 2) return null;

            var invite = _studyRoomManager.CreatePendingInvite(creator, invitedUsernames);
            
            // Add creator to a special group for this invite to receive updates
            await Groups.AddToGroupAsync(Context.ConnectionId, $"study_invite_{invite.Id}");

            // Broadcast invite to each invited user
            foreach (var username in invitedUsernames)
            {
                var connId = _userManager.GetConnectionId(username);
                if (connId != null)
                {
                    await Groups.AddToGroupAsync(connId, $"study_invite_{invite.Id}");
                    await Clients.Client(connId).SendAsync("StudyRoomInviteReceived", new {
                        inviteId = invite.Id,
                        inviter = creator,
                        members = allMembers
                    });
                }
            }

            return invite.Id;
        }

        public async Task<bool> RespondToStudyRoomInvite(string inviteId, bool accept)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return false;

            var invite = _studyRoomManager.GetPendingInvite(inviteId);
            if (invite == null) return false;

            if (accept)
            {
                if (_studyRoomManager.AcceptInvite(inviteId, username))
                {
                    await Clients.Group($"study_invite_{inviteId}").SendAsync("StudyRoomInviteAccepted", inviteId, username);

                    if (invite.AllAccepted)
                    {
                        var allMembers = new List<string>(invite.InvitedMembers) { invite.Creator };
                        var room = _studyRoomManager.CreateRoom(invite.Creator, allMembers);

                        foreach (var member in allMembers)
                        {
                            var connId = _userManager.GetConnectionId(member);
                            if (connId != null)
                            {
                                await Groups.AddToGroupAsync(connId, $"study_{room.Id}");
                                // Clean up the invite group
                                await Groups.RemoveFromGroupAsync(connId, $"study_invite_{inviteId}");
                            }
                        }

                        await Clients.Group($"study_{room.Id}").SendAsync("StudyRoomCreated", room);
                        _studyRoomManager.RemovePendingInvite(inviteId);
                    }
                    return true;
                }
            }
            else
            {
                if (_studyRoomManager.DeclineInvite(inviteId, username))
                {
                    await Clients.Group($"study_invite_{inviteId}").SendAsync("StudyRoomInviteDeclined", inviteId, username);
                    _studyRoomManager.RemovePendingInvite(inviteId);
                    return true;
                }
            }

            return false;
        }

        public async Task LeaveStudyRoom(string roomId)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var (room, wasOwner, deleted) = _studyRoomManager.RemoveMember(roomId, username);
            if (room != null)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"study_{roomId}");
                
                if (deleted)
                {
                    await Clients.Group($"study_{roomId}").SendAsync("StudyRoomDeleted", roomId);
                    _aiMemory.ClearRoom(roomId);
                    _rateLimiter.ClearRoom(roomId);
                }
                else
                {
                    string? newOwner = wasOwner ? room.Owner : null;
                    await Clients.Group($"study_{roomId}").SendAsync("StudyRoomMemberLeft", roomId, username, newOwner);
                }
            }
        }

        public async Task SendStudyRoomMessage(string roomId, object payload)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var room = _studyRoomManager.GetRoom(roomId);
            if (room == null || !room.Members.Contains(username)) return;

            // Study room messages are plaintext (no E2EE)
            await GroupExcludingBlockers($"study_{roomId}", username).SendAsync("StudyRoomMessageReceived", roomId, username, payload);
        }

        public async Task PromptPingu(string roomId, string prompt)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var room = _studyRoomManager.GetRoom(roomId);
            if (room == null || !room.Members.Contains(username)) return;

            if (_studyRoomManager.IsExpired(roomId)) return;

            if (!_rateLimiter.TryConsume(roomId))
            {
                var resetSeconds = _rateLimiter.GetResetTimeSeconds(roomId);
                await Clients.Group($"study_{roomId}").SendAsync("PinguResponse", roomId, prompt, "I am busy! Try again in a moment. 🐧", DateTime.UtcNow);
                return;
            }

            // Broadcast that Pingu is thinking
            await Clients.Group($"study_{roomId}").SendAsync("PinguTyping", roomId, true);

            try
            {
                var history = _aiMemory.GetHistory(roomId);
                var response = await _llmService.GenerateResponseAsync(roomId, prompt, history);

                // Save to memory
                _aiMemory.AddMessage(roomId, "user", prompt);
                _aiMemory.AddMessage(roomId, "model", response);

                await Clients.Group($"study_{roomId}").SendAsync("PinguResponse", roomId, prompt, response, DateTime.UtcNow);
            }
            finally
            {
                await Clients.Group($"study_{roomId}").SendAsync("PinguTyping", roomId, false);
            }
        }

        public IEnumerable<StudyRoom> GetStudyRooms()
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return Enumerable.Empty<StudyRoom>();
            return _studyRoomManager.GetRoomsForUser(username);
        }
    }
}
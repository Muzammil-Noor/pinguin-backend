using Microsoft.AspNetCore.SignalR;
using Pinguin.Services;
using System.Collections.Concurrent;

namespace Pinguin.Hubs
{
    public class ChatHub : Hub
    {
        private readonly StudyRoomManager _studyRoomManager;
        private readonly StudyRoomAiMemory _aiMemory;
        private readonly StudyRoomRateLimiter _rateLimiter;
        private readonly ILlmService _llmService;

        // Store PUBLIC keys only (true E2EE)
        private static readonly ConcurrentDictionary<string, string> _publicKeys = new();

        public ChatHub(
            UserManager userManager, 
            ChatroomManager chatroomManager,
            StudyRoomManager studyRoomManager,
            StudyRoomAiMemory aiMemory,
            StudyRoomRateLimiter rateLimiter,
            ILlmService llmService)
        {
            _userManager = userManager;
            _chatroomManager = chatroomManager;
            _studyRoomManager = studyRoomManager;
            _aiMemory = aiMemory;
            _rateLimiter = rateLimiter;
            _llmService = llmService;
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
                                await Clients.All.SendAsync("RoomDeleted", room.Id);
                            }
                            else
                            {
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
            Console.WriteLine($"Message Received: {message}");
            if (username != null)
            {
                await Clients.All.SendAsync("MessageReceived", username, message);
            }
        }

        public async Task SendFile(string fileName, string fileData, string? toUser, string? caption = null)
        {
            var senderUsername = _userManager.GetUsername(Context.ConnectionId);
            if (senderUsername == null) return;

            if (string.IsNullOrEmpty(toUser))
            {
                // Global file
                await Clients.All.SendAsync("FileReceived", senderUsername, fileName, fileData, false, null, caption);
            }
            else
            {
                // Private file
                var targetConnectionId = _userManager.GetConnectionId(toUser);
                if (targetConnectionId != null)
                {
                    // Send to recipient
                    await Clients.Client(targetConnectionId).SendAsync("FileReceived", senderUsername, fileName, fileData, true, null, caption);
                }

                // Send back to caller so they see their own file
                await Clients.Caller.SendAsync("FileReceived", senderUsername, fileName, fileData, true, toUser, caption);
            }
        }

        // Pure encrypted relay
        public async Task SendPrivateMessage(string toUsername, object payload)
        {
            var senderUsername = _userManager.GetUsername(Context.ConnectionId);
            if (senderUsername == null) return;

            var targetConnectionId = _userManager.GetConnectionId(toUsername);
            if (targetConnectionId == null) return;

            await Clients.Client(targetConnectionId)
                .SendAsync("PrivateMessageReceived", senderUsername, payload);

            await Clients.Caller
                .SendAsync("PrivateMessageSent", toUsername, payload);
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
                    await Clients.All.SendAsync("RoomDeleted", roomId);
                }
                else
                {
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

            await Clients.Group(roomId).SendAsync("RoomMessageReceived", roomId, username, payload);
        }

        public async Task SendRoomFile(string roomId, string fileName, string fileData, string? caption = null)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username == null) return;

            var room = _chatroomManager.GetRoom(roomId);
            if (room == null || !room.Members.Contains(username)) return;

            await Clients.Group(roomId).SendAsync("RoomFileReceived", roomId, username, fileName, fileData, caption);
        }

        public IEnumerable<object> GetRooms()
        {
            return _chatroomManager.GetAllRooms();
        }

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
            await Clients.Group($"study_{roomId}").SendAsync("StudyRoomMessageReceived", roomId, username, payload);
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
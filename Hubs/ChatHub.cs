using Microsoft.AspNetCore.SignalR;
using Pinguin.Services;
using System.Collections.Concurrent;

namespace Pinguin.Hubs
{
    public class ChatHub : Hub
    {
        private readonly UserManager _userManager;

        // Store PUBLIC keys only (true E2EE)
        private static readonly ConcurrentDictionary<string, string> _publicKeys = new();

        public ChatHub(UserManager userManager)
        {
            _userManager = userManager;
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
    }
}
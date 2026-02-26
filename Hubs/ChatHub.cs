using Microsoft.AspNetCore.SignalR;
using Pinguin.Services;

namespace Pinguin.Hubs
{
    public class ChatHub : Hub
    {
        private readonly UserManager _userManager;

        public ChatHub(UserManager userManager)
        {
            _userManager = userManager;
        }

        public async Task<bool> JoinChat(string username)
        {
            var success = _userManager.TryAddUser(Context.ConnectionId, username);
            if (success)
            {
                await Clients.Others.SendAsync("UserJoined", username);
            }
            return success;
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
                await Clients.All.SendAsync("UserLeft", username);
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(string message)
        {
            var username = _userManager.GetUsername(Context.ConnectionId);
            if (username != null)
            {
                await Clients.All.SendAsync("MessageReceived", username, message);
            }
        }

        public async Task SendPrivateMessage(string toUsername, string message)
        {
            var senderUsername = _userManager.GetUsername(Context.ConnectionId);
            if (senderUsername != null)
            {
                var targetConnectionId = _userManager.GetConnectionId(toUsername);
                if (targetConnectionId != null)
                {
                    // Send to the target user
                    await Clients.Client(targetConnectionId).SendAsync("PrivateMessageReceived", senderUsername, message, false);
                    // Echo back to the sender
                    await Clients.Caller.SendAsync("PrivateMessageReceived", toUsername, message, true);
                }
            }
        }

        public async Task SendFile(string fileName, string fileData, string? toUsername)
        {
            var senderUsername = _userManager.GetUsername(Context.ConnectionId);
            if (senderUsername != null)
            {
                if (string.IsNullOrWhiteSpace(toUsername))
                {
                    // Global file
                    await Clients.All.SendAsync("FileReceived", senderUsername, fileName, fileData, null);
                }
                else
                {
                    // Private file
                    var targetConnectionId = _userManager.GetConnectionId(toUsername);
                    if (targetConnectionId != null)
                    {
                        // Send to the target user
                        await Clients.Client(targetConnectionId).SendAsync("FileReceived", senderUsername, fileName, fileData, senderUsername);
                        // Echo back to the sender
                        await Clients.Caller.SendAsync("FileReceived", senderUsername, fileName, fileData, toUsername);
                    }
                }
            }
        }
    }
}
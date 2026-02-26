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
    }
}
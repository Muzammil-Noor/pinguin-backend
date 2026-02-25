using Microsoft.AspNetCore.SignalR;
using Pinguin.Backend.Services;

namespace Pinguin.Backend.Hubs;

public class ChatHub : Hub
{
    private readonly UserManager _userManager;

    public ChatHub(UserManager userManager)
    {
        _userManager = userManager;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = _userManager.RemoveUser(Context.ConnectionId);
        if (username != null)
        {
            // Notify others that user left
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

using Microsoft.AspNetCore.SignalR;
using Pinguin.Hubs;

namespace Pinguin.Services;

/// <summary>
/// Background service that periodically checks for expired study rooms and pending invitations.
/// Ticks every 30 seconds to destroy expired rooms and clean up stale invites.
/// </summary>
public class StudyRoomExpiryService : BackgroundService
{
    private readonly StudyRoomManager _studyRoomManager;
    private readonly StudyRoomAiMemory _aiMemory;
    private readonly StudyRoomRateLimiter _rateLimiter;
    private readonly IHubContext<ChatHub> _hubContext;

    public StudyRoomExpiryService(
        StudyRoomManager studyRoomManager,
        StudyRoomAiMemory aiMemory,
        StudyRoomRateLimiter rateLimiter,
        IHubContext<ChatHub> hubContext)
    {
        _studyRoomManager = studyRoomManager;
        _aiMemory = aiMemory;
        _rateLimiter = rateLimiter;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            // Check for expired study rooms
            var expiredRooms = _studyRoomManager.GetExpiredRooms();
            foreach (var room in expiredRooms)
            {
                // Notify all members that the room has expired
                await _hubContext.Clients.Group($"study_{room.Id}")
                    .SendAsync("StudyRoomExpired", room.Id, stoppingToken);

                // Clean up resources
                _studyRoomManager.DestroyRoom(room.Id);
                _aiMemory.ClearRoom(room.Id);
                _rateLimiter.ClearRoom(room.Id);
            }

            // Check for expired pending invitations
            var expiredInvites = _studyRoomManager.GetExpiredInvites();
            foreach (var invite in expiredInvites)
            {
                // Notify creator that the invitation expired
                await _hubContext.Clients.Group($"study_invite_{invite.Id}")
                    .SendAsync("StudyRoomInviteExpired", invite.Id, stoppingToken);

                _studyRoomManager.RemovePendingInvite(invite.Id);
            }
        }
    }
}

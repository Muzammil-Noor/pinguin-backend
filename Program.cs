using Pinguin.Backend.Hubs;
using Pinguin.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSignalR();
builder.Services.AddSingleton<UserManager>();

// Setup CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:4200") // Angular default port
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors("AllowFrontend");

// Minimal API for username validation
app.MapPost("/username/validate", (ValidateUsernameRequest request, UserManager userManager) =>
{
    if (string.IsNullOrWhiteSpace(request.Username))
    {
         return Results.BadRequest(new { Available = false, Message = "Username cannot be empty." });
    }

    var available = !userManager.GetAllUsers().Contains(request.Username);
    return Results.Ok(new { Available = available });
});

// Minimal API to set the username for a connection (called after SignalR connects)
app.MapPost("/username/set", (SetUsernameRequest request, UserManager userManager) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.ConnectionId))
    {
        return Results.BadRequest(new { Success = false });
    }

    var success = userManager.TryAddUser(request.ConnectionId, request.Username);
    if (success)
    {
        // Hub context would ideally send a 'UserJoined' message here, but simpler to let the client connect first.
        return Results.Ok(new { Success = true });
    }
    return Results.Ok(new { Success = false });
});


// Map SignalR Hub
app.MapHub<ChatHub>("/chathub");

app.Run();

public record ValidateUsernameRequest(string Username);
public record SetUsernameRequest(string ConnectionId, string Username);

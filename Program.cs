using Pinguin.Hubs;
using Pinguin.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Add services to the container.
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 100 * 1024 * 1024; // 100 MB
});
builder.Services.AddSingleton<UserManager>();
builder.Services.AddSingleton<ChatroomManager>();
builder.Services.AddRazorPages();

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

app.UseStaticFiles();

// Map SignalR Hub
app.MapHub<ChatHub>("/chathub");

app.MapRazorPages();

app.Run();

public static class Globals
{
    public static readonly DateTime ServerStart = DateTime.Now;
}

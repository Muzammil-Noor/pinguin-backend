using Microsoft.AspNetCore.SignalR;
using Pinguin.Services;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;

namespace Pinguin.Hubs
{
    public class ChatHub : Hub
    {
        private readonly UserManager _userManager;
        // Store RSA key pairs per connection
        private static readonly ConcurrentDictionary<string, RSA> _rsaKeys = new();

        public ChatHub(UserManager userManager)
        {
            _userManager = userManager;
        }

        public override async Task OnConnectedAsync()
        {
            // Generate RSA key pair for this connection
            var rsa = RSA.Create(2048); 
            _rsaKeys[Context.ConnectionId] = rsa;

            // Export keys as PEM strings
            var publicKeyPem = ExportPublicKeyPem(rsa);
            var privateKeyPem = ExportPrivateKeyPem(rsa);

            // Send private key to the connected client
            await Clients.Caller.SendAsync("ReceivePrivateKey", privateKeyPem);

            // Broadcast public key to all clients (including the new one)
            await Clients.All.SendAsync("PublicKeyAdded", Context.ConnectionId, publicKeyPem);

            await base.OnConnectedAsync();
        }

        public async Task<bool> JoinChat(string username)
        {
            var success = _userManager.TryAddUser(Context.ConnectionId, username);
            if (!success) return false;

            await Clients.Others.SendAsync("UserJoined", username);

            //Send new user's public key to everyone
            if (_rsaKeys.TryGetValue(Context.ConnectionId, out var newUserRsa))
            {
                var newUserPublicKey = ExportPublicKeyPem(newUserRsa);
                await Clients.All.SendAsync("UserPublicKey", username, newUserPublicKey);
            }

            //Send ALL EXISTING users' public keys to the NEW user
            foreach (var kvp in _rsaKeys)
            {
                var connectionId = kvp.Key;
                var rsa = kvp.Value;

                if (connectionId == Context.ConnectionId) continue;

                var existingUsername = _userManager.GetUsername(connectionId);
                if (existingUsername != null)
                {
                    var existingPublicKey = ExportPublicKeyPem(rsa);
                    await Clients.Caller.SendAsync("UserPublicKey", existingUsername, existingPublicKey);
                }
            }
            return true;
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

            // Clean up RSA key
            _rsaKeys.TryRemove(Context.ConnectionId, out _);
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
            Console.WriteLine($"Sender: {senderUsername}, Message: {message}");
            if (senderUsername != null)
            {
                var targetConnectionId = _userManager.GetConnectionId(toUsername);
                if (targetConnectionId != null)
                {
                    // Forward encrypted message; no decryption on server
                    await Clients.Client(targetConnectionId).SendAsync("PrivateMessageReceived", senderUsername, message, false);
                    // Echo back to sender (optional for UI consistency)
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

        // Helper methods to export PEM strings
        private static string ExportPublicKeyPem(RSA rsa)
        {
            var publicKey = rsa.ExportSubjectPublicKeyInfo();
            var sb = new StringBuilder();
            sb.AppendLine("-----BEGIN PUBLIC KEY-----");
            sb.AppendLine(Convert.ToBase64String(publicKey, Base64FormattingOptions.InsertLineBreaks));
            sb.AppendLine("-----END PUBLIC KEY-----");
            return sb.ToString();
        }

        private static string ExportPrivateKeyPem(RSA rsa)
        {
            var privateKey = rsa.ExportPkcs8PrivateKey();
            var sb = new StringBuilder();
            sb.AppendLine("-----BEGIN PRIVATE KEY-----");
            sb.AppendLine(Convert.ToBase64String(privateKey, Base64FormattingOptions.InsertLineBreaks));
            sb.AppendLine("-----END PRIVATE KEY-----");
            return sb.ToString();
        }
    }
}
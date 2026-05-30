using Google.GenAI;
using Google.GenAI.Types;

namespace Pinguin.Services;

public class GeminiLlmService : ILlmService
{
    private readonly Client _client;
    private const string ModelName = "gemini-2.0-flash";
    private const string SystemPrompt = Globals.SystemPrompt;
    public GeminiLlmService(IConfiguration configuration)
    {
        var apiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configuration["Gemini:ApiKey"]?? throw new InvalidOperationException("GEMINI_API_KEY environment variable is not set.");
        _client = new Client(apiKey: apiKey);
    }

    public async Task<string> GenerateResponseAsync(string studyRoomId, string userPrompt, List<AiMessage> conversationHistory)
    {
        try
        {
            var contents = new List<Content>();
            foreach (var msg in conversationHistory)
            {
                contents.Add(new Content
                {
                    Role = msg.Role == "model" ? "model" : "user",
                    Parts =[new Part { Text = msg.Content }]
                });
            }

            contents.Add(new Content
            {
                Role = "user",
                Parts =[new Part { Text = userPrompt }]
            });

            var response = await _client.Models.GenerateContentAsync(
                model: ModelName,
                contents: contents,
                config: new GenerateContentConfig
                {
                    SystemInstruction = new Content{ Parts =[ new Part { Text = SystemPrompt } ] },
                    Temperature = 0.7f,
                    MaxOutputTokens = 1024,
                    TopP = 0.9f
                });

            var text = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            Random random = new Random();
            string err = Globals.NoTextMessages[random.Next(Globals.NoTextMessages.Length)];
            return string.IsNullOrWhiteSpace(text) ? err : text.Trim();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Gemini API exception: {ex}");
            Random random = new Random();
            return Globals.ErrorMessages[random.Next(Globals.ErrorMessages.Length)];
        }
    }
}
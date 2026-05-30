using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Pinguin.Services
{
    public class HuggingFaceLlmService : ILlmService
    {
        private readonly HttpClient _httpClient;
        private const string SystemPrompt = Globals.SystemPrompt;
        public HuggingFaceLlmService()
        {
            var apiKey = Environment.GetEnvironmentVariable("HF_API_KEY") ?? throw new InvalidOperationException("HF_API_KEY environment variable is not set.");
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PinguinApp/1.0");
        }

        public async Task<string> GenerateResponseAsync(string studyRoomId, string userPrompt, List<AiMessage> conversationHistory)
        {
            try
            {
                var messages = new List<object>
                {
                    new
                    {
                        role = "system",
                        content = SystemPrompt
                    }
                };

                foreach (var msg in conversationHistory)
                {
                    messages.Add(new
                    {
                        role = msg.Role == "model" ? "assistant" : "user",
                        content = msg.Content
                    });
                }

                messages.Add(new
                {
                    role = "user",
                    content = userPrompt
                });

                var requestBody = new
                {
                    model = "Qwen/Qwen2.5-72B-Instruct",
                    messages = messages,
                    max_tokens = 150,
                    temperature = 0.7
                };

                var json = JsonSerializer.Serialize(requestBody);

                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://router.huggingface.co/v1/chat/completions", content);
                Random random = new Random();

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();

                    Console.Error.WriteLine($"HF API error: {response.StatusCode}");
                    Console.Error.WriteLine(errorBody);

                    return Globals.ErrorMessages[random.Next(Globals.ErrorMessages.Length)];
                }

                var resultJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(resultJson);
                var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

                string err = Globals.NoTextMessages[random.Next(Globals.NoTextMessages.Length)];
                return string.IsNullOrWhiteSpace(text) ? err : text.Trim();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Exception: {ex}");
                Random random = new Random();
                return Globals.ErrorMessages[random.Next(Globals.ErrorMessages.Length)];
            }
        }
    }
}
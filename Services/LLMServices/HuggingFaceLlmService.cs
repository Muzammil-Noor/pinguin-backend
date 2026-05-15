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
        private const string SystemPrompt = @"
You are Pingu, a friendly study assistant.
Guide students to understanding, never give direct answers.
Be concise, warm, and encouraging.
";

        public HuggingFaceLlmService()
        {
            var apiKey = Environment.GetEnvironmentVariable("HF_API_KEY")
                         ?? throw new InvalidOperationException("HF_API_KEY environment variable is not set.");

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public async Task<string> GenerateResponseAsync(
            string studyRoomId,
            string userPrompt,
            List<AiMessage> conversationHistory)
        {
            try
            {
                // Build the prompt with system instruction + conversation
                string fullPrompt = SystemPrompt + "\n";
                foreach (var msg in conversationHistory)
                {
                    fullPrompt += (msg.Role == "model" ? "Assistant: " : "User: ") + msg.Content + "\n";
                }
                fullPrompt += "User: " + userPrompt + "\nAssistant: ";

                var requestBody = new
                {
                    inputs = fullPrompt,
                    parameters = new
                    {
                        max_new_tokens = 150,
                        temperature = 0.7
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Call a public model endpoint
                var response = await _httpClient.PostAsync(
                    "https://api-inference.huggingface.co/models/gpt2",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"Hugging Face API error: {response.StatusCode}");
                    return "Oops, something went wrong on my end. Try again shortly! 🐧";
                }

                var resultJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(resultJson);
                var text = doc.RootElement[0].GetProperty("generated_text").GetString();

                return string.IsNullOrWhiteSpace(text)
                    ? "Hmm, I couldn't think of an answer. Could you try rephrasing? 🐧"
                    : text.Trim();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Exception: {ex.Message}");
                return "Oops, something went wrong on my end. Try again shortly! 🐧";
            }
        }
    }
}
namespace Pinguin.Services;

public class AiMessage
{
    public string Role { get; set; } = string.Empty;  // "user" or "model"
    public string Content { get; set; } = string.Empty;
}

public interface ILlmService
{
    Task<string> GenerateResponseAsync(
        string studyRoomId,
        string userPrompt,
        List<AiMessage> conversationHistory
    );
}

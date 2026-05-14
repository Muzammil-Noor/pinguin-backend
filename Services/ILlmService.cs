namespace Pinguin.Services;

public class AiMessage
{
    public string Role { get; set; } = string.Empty;  // "user" or "model"
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Abstract LLM interface. Implement this to swap out the AI provider.
/// Gemini is the default implementation, but any LLM can be plugged in.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Generates a response from the AI given a user prompt and conversation history.
    /// The implementation must enforce the "guide, don't give direct answers" behavior via system prompt.
    /// </summary>
    Task<string> GenerateResponseAsync(
        string studyRoomId,
        string userPrompt,
        List<AiMessage> conversationHistory
    );
}

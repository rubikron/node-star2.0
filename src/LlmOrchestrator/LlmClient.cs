using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmOrchestrator;

public class LlmClient
{
    private readonly HttpClient _http = new();

    public async Task<string> SendAsync(
        LlmSettings settings,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        var baseUrl = settings.BaseUrl.TrimEnd('/');
        var payload = new ChatRequest(
            settings.Model,
            messages.Select(m => new MessageDto(m.Role, m.Content)).ToArray());

        var response = await _http.PostAsJsonAsync($"{baseUrl}/v1/chat/completions", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
        return result?.Choices?[0].Message.Content ?? string.Empty;
    }

    private record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] MessageDto[] Messages);

    private record MessageDto(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] Choice[] Choices);

    private record Choice(
        [property: JsonPropertyName("message")] MessageDto Message);
}

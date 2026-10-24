using System.ClientModel;
using Microsoft.Extensions.AI;
using OllamaSharp;
using System.ClientModel.Primitives;
using OpenAI;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using ChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;
using Rag.Core;
using Rag.Generation;

namespace Rag.Hosting;

/// <summary>
/// Builds provider clients behind the Microsoft.Extensions.AI interfaces, so switching
/// Gemini ↔ Ollama ↔ any OpenAI-compatible API (GitHub Models, Groq) is a config change.
/// </summary>
public static class AiClientFactory
{
    private const string GeminiOpenAiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public static IChatClient CreateChat(ChatModelOptions o) => o.Provider.ToLowerInvariant() switch
    {
        "ollama" => new OllamaApiClient(new Uri(o.Endpoint ?? "http://localhost:11434"), o.Model),
        "gemini" => WithReasoning(OpenAiCompatible(o.Endpoint ?? GeminiOpenAiEndpoint, Key(o.ApiKey, "GEMINI_API_KEY", "Gemini"), o)
            .GetChatClient(o.Model).AsIChatClient(), o),
        "openai" => WithReasoning(OpenAiCompatible(o.Endpoint ?? "https://api.openai.com/v1", Key(o.ApiKey, "OPENAI_API_KEY", "OpenAI-compatible"), o)
            .GetChatClient(o.Model).AsIChatClient(), o),
        _ => throw new NotSupportedException($"Unknown chat provider '{o.Provider}'"),
    };

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbedding(EmbeddingOptions o) => o.Provider.ToLowerInvariant() switch
    {
        "ollama" => new OllamaApiClient(new Uri(o.Endpoint), o.Model),
        "gemini" => OpenAiCompatible(GeminiOpenAiEndpoint, Key(o.ApiKey, "GEMINI_API_KEY", "Gemini"))
            .GetEmbeddingClient(o.Model).AsIEmbeddingGenerator(),
        _ => throw new NotSupportedException($"Unknown embedding provider '{o.Provider}'"),
    };

    /// <summary>
    /// Few retries and a bounded timeout: by default the SDK retries 429s several times with growing waits,
    /// so a used-up free-tier quota made every question wait seconds just to fail.
    /// </summary>
    private static OpenAIClient OpenAiCompatible(string endpoint, string key, ChatModelOptions? o = null) =>
        new(new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            RetryPolicy = new ClientRetryPolicy(o?.MaxRetries ?? 1),
            NetworkTimeout = TimeSpan.FromSeconds(o?.TimeoutSeconds ?? 60),
        });

#pragma warning disable OPENAI001 // ReasoningEffortLevel is marked "evaluation" in the SDK; it maps to the standard reasoning_effort field.
    /// <summary>Asks reasoning models to think less before short, factual answers (sent as reasoning_effort).</summary>
    private static IChatClient WithReasoning(IChatClient client, ChatModelOptions o) =>
        string.IsNullOrWhiteSpace(o.ReasoningEffort) ? client
        : client.AsBuilder().ConfigureOptions(options => options.RawRepresentationFactory ??= _ =>
            new ChatCompletionOptions { ReasoningEffortLevel = new ChatReasoningEffortLevel(o.ReasoningEffort) }).Build();
#pragma warning restore OPENAI001

    private static string Key(string? configured, string envVar, string provider) =>
        !string.IsNullOrWhiteSpace(configured) ? configured
        : Environment.GetEnvironmentVariable(envVar)
          ?? throw new InvalidOperationException(
              $"{provider} API key missing. Run: dotnet user-secrets set \"AI:Chat:ApiKey\" \"<key>\" --id advanced-rag  (or set {envVar})");
}

/// <summary>Creates the real client on first use, so a missing API key only fails when an answer is requested.</summary>
public sealed class LazyChatClient(Func<IChatClient> factory) : IChatClient
{
    private readonly Lazy<IChatClient> _inner = new(factory);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.Value.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.Value.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.IsValueCreated ? _inner.Value.GetService(serviceType, serviceKey) : null;

    public void Dispose()
    {
        if (_inner.IsValueCreated) _inner.Value.Dispose();
    }
}

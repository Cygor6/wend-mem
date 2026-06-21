using Microsoft.Extensions.AI;
using Wendmem.Experiences;
using Wendmem.Options;
using Wendmem.Services;

namespace Wendmem.Tests;

/// <summary>
/// Covers the configurable thinking-parameter resolver (parsing + provider defaults) and
/// verifies that LlmService forwards the resolved params to the chat client on every call.
/// </summary>
sealed class DisableThinkingTests
{
    [Test]
    public async Task Parse_blank_returns_null()
    {
        await Assert.That(DisableThinkingParams.Parse("")).IsNull();
        await Assert.That(DisableThinkingParams.Parse("   ")).IsNull();
        await Assert.That(DisableThinkingParams.Parse(null)).IsNull();
    }

    [Test]
    public async Task Parse_garbage_returns_null()
    {
        await Assert.That(DisableThinkingParams.Parse("not json")).IsNull();
        await Assert.That(DisableThinkingParams.Parse("{broken")).IsNull();
    }

    [Test]
    public async Task Parse_non_object_returns_null()
    {
        await Assert.That(DisableThinkingParams.Parse("[]")).IsNull();
        await Assert.That(DisableThinkingParams.Parse("true")).IsNull();
    }

    [Test]
    public async Task Parse_nested_object()
    {
        var dict = DisableThinkingParams.Parse("""{"thinking":{"type":"disabled"}}""");
        await Assert.That(dict).IsNotNull();
        await Assert.That(dict!.ContainsKey("thinking")).IsTrue();
        var inner = (System.Text.Json.Nodes.JsonObject)dict["thinking"]!;
        await Assert.That(inner["type"]!.AsValue().GetValue<string>()).IsEqualTo("disabled");
    }

    [Test]
    public async Task Parse_boolean_value()
    {
        var dict = DisableThinkingParams.Parse("""{"think":false}""");
        await Assert.That(dict).IsNotNull();
        var node = (System.Text.Json.Nodes.JsonNode)dict!["think"]!;
        await Assert.That(node.AsValue().GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Resolve_uses_configured_value_when_set()
    {
        var resolved = DisableThinkingParams.Resolve(
            LlmProvider.LlamaCpp,
            """{"chat_template_kwargs":{"enable_thinking":false}}""");
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.ContainsKey("chat_template_kwargs")).IsTrue();
    }

    [Test]
    public async Task Resolve_falls_back_to_default_when_unparseable()
    {
        var resolved = DisableThinkingParams.Resolve(LlmProvider.Ollama, "garbage");
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.ContainsKey("think")).IsTrue();
    }

    [Test]
    public async Task Default_ZAi_sends_thinking_disabled()
    {
        var d = DisableThinkingParams.DefaultFor(LlmProvider.ZAi);
        await Assert.That(d).IsNotNull();
        await Assert.That(d!.ContainsKey("thinking")).IsTrue();
    }

    [Test]
    public async Task Default_LlamaCpp_sends_nothing()
    {
        await Assert.That(DisableThinkingParams.DefaultFor(LlmProvider.LlamaCpp)).IsNull();
    }

    [Test]
    public async Task DisableThinkingJsonForActive_picks_active_provider_value()
    {
        var opts = new LlmOptions
        {
            Provider = LlmProvider.LlamaCpp,
            LlamaCpp = new LlamaCppOptions
            {
                DisableThinkingJson = """{"chat_template_kwargs":{"enable_thinking":false}}""",
            },
        };
        await Assert.That(opts.DisableThinkingJsonForActive())
            .IsEqualTo("""{"chat_template_kwargs":{"enable_thinking":false}}""");
    }

    [Test]
    public async Task DisableThinkingJsonForActive_empty_when_unset()
    {
        var opts = new LlmOptions { Provider = LlmProvider.ZAi };
        await Assert.That(opts.DisableThinkingJsonForActive()).IsEqualTo("");
    }

    [Test]
    public async Task LlmService_forwards_disable_thinking_params_to_chat_client()
    {
        var recording = new RecordingChatClient();
        var params_ = new AdditionalPropertiesDictionary { ["think"] = false };
        var sut = new LlmService(recording, "fake", params_);

        await sut.CompleteAsync("hello", CancellationToken.None);

        await Assert.That(recording.LastOptions).IsNotNull();
        await Assert.That(recording.LastOptions!.AdditionalProperties).IsNotNull();
        await Assert.That(recording.LastOptions!.AdditionalProperties!.ContainsKey("think")).IsTrue();
    }

    [Test]
    public async Task LlmService_omits_options_when_no_thinking_params()
    {
        // A non-reasoning model resolves to null params; behavior must be unchanged (no
        // ChatOptions constructed), so older call paths are not disturbed.
        var recording = new RecordingChatClient();
        var sut = new LlmService(recording, "fake", disableThinking: null);

        await sut.CompleteAsync("hello", CancellationToken.None);

        await Assert.That(recording.LastOptions).IsNull();
    }

    sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
        public ValueTask DisposeAsync() { GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
    }
}

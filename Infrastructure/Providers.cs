using System.ClientModel;
using Anthropic.SDK;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mscc.GenerativeAI.Microsoft;

namespace Imp.Infrastructure;

// Compile-time dispatch over the providers nb supports. Each section in
// ChatProviders[] has a Name that matches a case below. Mirrors nb's provider
// code but drops the IChatClientProvider interface and AssemblyLoadContext
// machinery — we don't need runtime plugin loading.
public static class Providers
{
    public static IChatClient Create(IConfiguration root)
        => CreateForProvider(root, root["ActiveProvider"]
            ?? throw new InvalidOperationException("ActiveProvider is not set in configuration."));

    // Build a chat client for a named ChatProvider entry. Used by `imp wiki`
    // to construct an orchestrator-role client (Wiki:Provider names a
    // ChatProvider that may differ from the research executor's provider —
    // typically a higher-context-tolerance model for synthesis).
    public static IChatClient CreateForProvider(IConfiguration root, string providerName)
    {
        var section = root.GetSection("ChatProviders").GetChildren()
            .FirstOrDefault(p => string.Equals(p["Name"], providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Provider '{providerName}' not found in ChatProviders[].");

        return providerName.ToLowerInvariant() switch
        {
            "azurefoundry" => CreateAzureFoundry(section),
            "azureopenai" => CreateAzureOpenAI(section),
            "openai" => CreateOpenAI(section),
            "anthropic" => CreateAnthropic(section),
            "gemini" => CreateGemini(section),
            "qwen" => CreateQwen(section),
            // GLM (and any other model served over a local OpenAI-compatible
            // endpoint, e.g. llama.cpp) uses the same client shape as Qwen.
            "glm" => CreateQwen(section),
            _ => throw new InvalidOperationException($"Unknown provider: {providerName}"),
        };
    }

    static IChatClient CreateAzureFoundry(IConfiguration cfg)
    {
        var endpoint = Required(cfg, "Endpoint");
        var apiKey = Required(cfg, "ApiKey");
        var model = Required(cfg, "Model");

        // Accept either the resource root or the full deployment URL —
        // the Azure SDK only wants the resource base (scheme + host).
        var parsed = new Uri(endpoint);
        var baseUri = new Uri($"{parsed.Scheme}://{parsed.Authority}/");

        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);
        var azureClient = new AzureOpenAIClient(baseUri, new AzureKeyCredential(apiKey), options);
        return azureClient.GetOpenAIResponseClient(model).AsIChatClient();
    }

    static IChatClient CreateAzureOpenAI(IConfiguration cfg)
    {
        var endpoint = Required(cfg, "Endpoint");
        var apiKey = Required(cfg, "ApiKey");
        var deployment = cfg["ChatDeploymentName"] ?? cfg["Model"] ?? "o4-mini";

        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), options);
        return azureClient.GetChatClient(deployment).AsIChatClient();
    }

    static IChatClient CreateOpenAI(IConfiguration cfg)
    {
        var apiKey = Required(cfg, "ApiKey");
        var model = cfg["Model"] ?? "gpt-4o-mini";
        return new OpenAI.Chat.ChatClient(model, apiKey).AsIChatClient();
    }

    static IChatClient CreateAnthropic(IConfiguration cfg)
    {
        var apiKey = Required(cfg, "ApiKey");
        var model = cfg["Model"] ?? Anthropic.SDK.Constants.AnthropicModels.Claude37Sonnet;
        var client = new AnthropicClient(apiKey);
        return new ChatClientBuilder(client.Messages)
            .ConfigureOptions(o =>
            {
                o.ModelId = model;
                // Anthropic's API requires max_tokens; MEA doesn't set a default.
                o.MaxOutputTokens ??= 4096;
            })
            .Build();
    }

    static IChatClient CreateGemini(IConfiguration cfg)
    {
        var apiKey = Required(cfg, "ApiKey");
        var model = cfg["Model"] ?? "gemini-2.0-flash-exp";
        return new GeminiChatClient(apiKey, model);
    }

    // Qwen (and any other Qwen3-Coder-class model served over an
    // OpenAI-compatible endpoint). Tested shapes:
    //   - DashScope cloud: https://dashscope.aliyuncs.com/compatible-mode/v1
    //     (intl variant: https://dashscope-intl.aliyuncs.com/compatible-mode/v1)
    //   - Ollama local: http://localhost:11434/v1 with any non-empty ApiKey
    //   - vLLM / sglang: the deployment's /v1 base, with whatever key the
    //     server enforces (often blank/dummy).
    // The model field is the served name — `qwen3-coder-plus` on DashScope,
    // `qwen3-coder:30b` on a typical Ollama install, etc.
    static IChatClient CreateQwen(IConfiguration cfg)
    {
        var endpoint = Required(cfg, "Endpoint");
        var apiKey = Required(cfg, "ApiKey");
        var model = Required(cfg, "Model");

        // Local inference (Vulkan / ROCm / CPU) is much slower than the
        // hosted SDK's default 100s per-request timeout assumes — a single
        // turn with reasoning + tool calls can exceed 5 minutes on
        // consumer-AMD setups. Configurable via NetworkTimeoutSeconds;
        // default 600s (10 min) is generous for any sane local-server pace.
        var timeoutSeconds = int.TryParse(cfg["NetworkTimeoutSeconds"], out var t) && t > 0 ? t : 600;

        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        return new OpenAI.Chat.ChatClient(
            model: model,
            credential: new ApiKeyCredential(apiKey),
            options: options).AsIChatClient();
    }

    static string Required(IConfiguration cfg, string key) =>
        cfg[key] ?? throw new InvalidOperationException(
            $"Provider '{cfg["Name"]}' is missing required config key '{key}'.");
}

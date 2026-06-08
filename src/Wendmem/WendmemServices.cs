using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OpenAI;
using Wendmem.Experiences;
using Wendmem.Experiences.Extractors;
using Wendmem.Options;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Tools;
using Wendmem.ToolsMemory;
using Wendmem.Wiki;

namespace Wendmem;

static class WendmemServices
{
    public static IServiceCollection AddWendmemCore(this IServiceCollection services, IConfiguration configuration, string dbPath)
    {
        services.AddDistributedMemoryCache();

        services.Configure<ModelsOptions>(
            configuration.GetSection(ModelsOptions.SectionName));
        services.Configure<LlmOptions>(
            configuration.GetSection(LlmOptions.SectionName));
        services.Configure<ExperienceOptions>(
            configuration.GetSection(ExperienceOptions.SectionName));

        services
            .AddSingleton(new DuckDbConnectionFactory(dbPath))
            .AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = 500
            }))
            .AddSingleton<IEmbedder>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<ModelsOptions>>().Value;
                var basePath = Services.AssemblyPathResolver.BasePath;
                var modelPath = Path.GetFullPath(Path.Combine(basePath, opts.EmbeddingModel.OnnxPath));
                var tokenizerPath = Path.GetFullPath(Path.Combine(basePath, opts.EmbeddingModel.TokenizerPath));
                Console.Error.WriteLine($"Model path:     {modelPath}");
                Console.Error.WriteLine($"Tokenizer path: {tokenizerPath}");
                return new Services.LazyEmbedder(
                    modelPath, tokenizerPath,
                    opts.EmbeddingModel.MaxSequenceTokens,
                    opts.EmbeddingModel.EmbeddingDimension,
                    modelOutputDim: opts.EmbeddingModel.ModelOutputDimension);
            })
            .AddSingleton<AaakDialect>(_ =>
                new AaakDialect(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MemPalace"] = "MEM",
                    ["DuckDB"] = "DUC",
                    ["PalaceSearcher"] = "PAL",
                    ["WakeUp"] = "WAK",
                }))
            .AddSingleton<HallDetector>(sp =>
            {
                var section = sp.GetRequiredService<IConfiguration>()
                    .GetSection(HallsOptions.SectionName);
                var halls = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in section.GetChildren())
                {
                    var keywords = child.GetChildren()
                        .Select(c => c.Value)
                        .Where(v => v is not null)
                        .Select(v => v!)
                        .ToArray();
                    if (keywords.Length > 0)
                        halls[child.Key] = keywords;
                }
                return new HallDetector(halls);
            })
            .AddSingleton<WalLogger>()
            .AddSingleton<ClosetStorage>()
            .AddSingleton<DrawerStorage>()
            .AddSingleton<EntityIndexService>()
            .AddSingleton<KnowledgeGraph>()
            .AddSingleton<KgResolver>()
            .AddHttpClient()
            .AddSingleton<WikiStorage>()
            .AddSingleton<NumericFactExtractor>()
            .AddSingleton<Chunkers.TopicShiftChunker>()
            .AddSingleton<FileMiner>()
            .AddSingleton<ConversationMiner>()
            .AddSingleton<PalaceConfig>()
            .AddSingleton<PalaceSearcher>()
            .AddSingleton<Sweeper>()
            .AddSingleton<EntityRefinementService>()
            .AddSingleton<ActivityLog>()
            .AddSingleton<ImportanceScorer>()
            .AddSingleton<PendingUpdateService>()
            .AddSingleton<WikiLinter>();

        var chatBuilder = services.AddChatClient(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            var (_, endpoint, model, apiKey, _) = opts.ResolveActive();

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    $"LLM API key is missing for provider '{opts.Provider}'. " +
                    $"Configure it via the source reported by startup validation.");

            Console.Error.WriteLine($"[LLM] Provider={opts.Provider} Endpoint={endpoint} Model={model}");

            return new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetChatClient(model)
                .AsIChatClient();
        });

        chatBuilder.UseDistributedCache();
        chatBuilder.Use(client => new FunctionInvokingChatClient(client) { IncludeDetailedErrors = true });
        chatBuilder.UseLogging();

        services.AddSingleton<LlmService>(sp =>
        {
            var chat = sp.GetRequiredService<IChatClient>();
            var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            var (_, _, model, _, _) = opts.ResolveActive();
            return new LlmService(chat, model);
        });

        services
            .AddSingleton<TaskMemoryStorage>()
            .AddSingleton<SuccessExtractor>()
            .AddSingleton<FailureExtractor>()
            .AddSingleton<ComparativeExtractor>()
            .AddSingleton<MemoryValidator>()
            .AddSingleton<MemoryDeduplicator>()
            .AddSingleton<ExperienceOptions>(sp =>
                sp.GetRequiredService<IOptions<ExperienceOptions>>().Value)
            .AddSingleton<ExperienceDistiller>()
            .AddSingleton<ExperienceRetriever>()
            .AddSingleton<ExperienceRefinement>()
            .AddSingleton<ToolMemoryStorage>()
            .AddSingleton<ToolMemoryDistiller>()
            .AddSingleton<EpisodeStorage>()
            .AddSingleton<SkillStorage>()
            .AddSingleton<ReflectionDraftStorage>()
            .AddSingleton<ReflectionService>()
            .AddSingleton<GraphDataService>();

        return services;
    }

    public static IMcpServerBuilder AddWendmemTools(this IMcpServerBuilder builder)
    {
        builder
            .WithTools<DrawerTools>()
            .WithTools<KnowledgeGraphTools>()
            .WithTools<WikiTools>()
            .WithTools<WikiMaintenanceTools>()
            .WithTools<EpisodeTools>()
            .WithTools<SkillTools>();

        builder.WithListResourcesHandler((ctx, ct) =>
        {
            var result = new ListResourcesResult
            {
                Resources =
                [
                    new ModelContextProtocol.Protocol.Resource
                    {
                        Uri = "palace://schema",
                        Name = "palace-schema",
                        Title = "Palace schema and conventions",
                        Description = "Auto-generated schema describing wings, rooms, routing keywords, " +
                                      "wiki conventions, and the standard agent workflow for this palace.",
                        MimeType = "text/markdown",
                    }
                ]
            };
            return new ValueTask<ListResourcesResult>(result);
        });

        builder.WithReadResourceHandler(async (ctx, ct) =>
        {
            if (ctx.Params?.Uri != "palace://schema")
                return new ReadResourceResult();

            var sp = ctx.Services;

            var schema = await PalaceSchemaResource.GetSchema(
                sp.GetRequiredService<PalaceConfig>(),
                sp.GetRequiredService<DrawerStorage>(),
                sp.GetRequiredService<KnowledgeGraph>(),
                sp.GetRequiredService<HallDetector>(),
                ct);

            return new ReadResourceResult
            {
                Contents =
                [
                    new TextResourceContents
                    {
                        Uri = "palace://schema",
                        MimeType = "text/markdown",
                        Text = schema,
                    }
                ]
            };
        });

        // Empty stubs for prompts and templates (not yet implemented)
        builder.WithListPromptsHandler((ctx, ct) =>
            new ValueTask<ListPromptsResult>(new ListPromptsResult()));
        builder.WithListResourceTemplatesHandler((ctx, ct) =>
            new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult()));

        return builder;
    }

    public static void ValidateStartup(IServiceProvider services)
    {
        var opts = services.GetRequiredService<IOptions<ModelsOptions>>().Value;
        var basePath = Services.AssemblyPathResolver.BasePath;
        var modelPath = Path.GetFullPath(Path.Combine(basePath, opts.EmbeddingModel.OnnxPath));
        var tokenizerPath = Path.GetFullPath(Path.Combine(basePath, opts.EmbeddingModel.TokenizerPath));
        Services.ModelValidator.EnsureFilesExist(modelPath, tokenizerPath);
        var embedder = services.GetRequiredService<IEmbedder>();
        Services.ModelValidator.EnsureEmbeddingDimension(embedder);

        var llmOpts = services.GetRequiredService<IOptions<LlmOptions>>().Value;
        var (_, _, _, apiKey, keySource) = llmOpts.ResolveActive();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"LLM API key is missing for provider '{llmOpts.Provider}'. " +
                $"Expected key from {keySource}.");

        Console.Error.WriteLine($"[LLM] API key loaded from {keySource} for provider {llmOpts.Provider}");

        _ = services.GetRequiredService<IChatClient>();
        Console.Error.WriteLine($"[LLM] IChatClient materialized successfully for provider {llmOpts.Provider}");
    }
}

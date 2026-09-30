using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using AgentMemory.Processing;
using AgentMemory.Retrieval;
using AgentMemory.Storage;
using Microsoft.Extensions.Http.Resilience;

namespace AgentMemory;

public static class ServiceRegistration
{
    /// <summary>
    /// Registers the OpenRouter-backed services (typed HTTP clients with a resilience
    /// handler), repositories, and the capture/retrieval pipeline. Each service interface
    /// must be registered exactly once: a second <c>AddScoped</c> for an interface that
    /// already has a typed-client registration silently replaces it and drops the
    /// resilience handler.
    /// </summary>
    public static IServiceCollection AddAgentMemoryServices(this IServiceCollection services)
    {
        services.AddHttpClient<IClassificationService, ClassificationService>(client => { })
            .AddStandardResilienceHandler(options => ConfigureRetries(options, maxAttempts: 3));

        services.AddHttpClient<IEmbeddingService, EmbeddingService>(client => { })
            .AddStandardResilienceHandler(options => ConfigureRetries(options, maxAttempts: 3));

        services.AddHttpClient<IDistillationService, DistillationService>(client => { })
            .AddStandardResilienceHandler(options => ConfigureRetries(options, maxAttempts: 2));

        services.AddScoped<CaptureProcessor>();
        services.AddScoped<ICaptureRepository, CaptureRepository>();
        services.AddScoped<IMemoryRepository, MemoryRepository>();
        services.AddScoped<IRetrievalService, RetrievalService>();

        return services;
    }

    private static void ConfigureRetries(HttpStandardResilienceOptions options, int maxAttempts)
    {
        options.Retry.MaxRetryAttempts = maxAttempts;
        options.Retry.DelayGenerator = static args =>
        {
            var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, args.AttemptNumber));
            return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 10)));
        };
        options.Retry.ShouldRetryAfterHeader = true;
    }
}

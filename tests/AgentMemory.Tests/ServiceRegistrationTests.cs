using AgentMemory.Classification;
using AgentMemory.Distillation;
using AgentMemory.Embedding;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests;

public class ServiceRegistrationTests
{
    [Theory]
    [InlineData(typeof(IClassificationService))]
    [InlineData(typeof(IEmbeddingService))]
    [InlineData(typeof(IDistillationService))]
    public void OpenRouter_Services_Are_Registered_Once_As_Typed_Http_Clients(Type serviceType)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryServices();

        // A second registration for the same interface (e.g. a leftover AddScoped) overrides
        // the typed-client one and silently drops its resilience/retry handler.
        var descriptor = Assert.Single(services, d => d.ServiceType == serviceType);

        // Typed-client registrations are factory-based; a plain AddScoped<I, Impl>() is not.
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
    }
}

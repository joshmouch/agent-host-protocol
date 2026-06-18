// IServiceCollection integration. Lives in the Microsoft.Extensions.DependencyInjection
// namespace (the .NET convention for IServiceCollection extensions) so it surfaces with
// a single `using Microsoft.Extensions.DependencyInjection;`.
#nullable enable

using System;
using Microsoft.AgentHostProtocol;
using Microsoft.AgentHostProtocol.Hosts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration extensions for the Agent Host Protocol client.</summary>
public static class AhpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AHP serializer, client-id store, multi-host runtime, and the
    /// <see cref="IAhpClientFactory"/> used to create clients over a caller-supplied
    /// transport, and binds <see cref="ClientConfig"/>. Registrations use <c>TryAdd</c>,
    /// so a consumer can override any service (for example a custom
    /// <see cref="IClientIdStore"/>) by registering it before calling this. The
    /// <see cref="MultiHostClient"/> singleton is disposed by the container on shutdown
    /// (it is <see cref="System.IAsyncDisposable"/>), so no hosted service is required —
    /// but the provider MUST be disposed asynchronously (<c>await using</c> /
    /// <c>DisposeAsync</c>, as the generic host does); a synchronous
    /// <c>ServiceProvider.Dispose()</c> throws because the runtime is async-disposable-only.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureClient">
    /// Optional <see cref="ClientConfig"/> configuration. This applies to the
    /// <see cref="IAhpClientFactory"/> path (single clients); <see cref="MultiHostClient"/>
    /// hosts are configured per host via <c>HostConfig.ClientConfig</c> on <c>AddHostAsync</c>.
    /// </param>
    public static IServiceCollection AddAgentHostProtocol(
        this IServiceCollection services,
        Action<ClientConfig>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAhpSerializer>(SystemTextJsonAhpSerializer.Default);
        services.TryAddSingleton<IClientIdStore, InMemoryClientIdStore>();
        // Explicit factory so the IClientIdStore dependency is visible rather than
        // left to greedy selection between MultiHostClient's two constructors.
        services.TryAddSingleton(sp => new MultiHostClient(sp.GetRequiredService<IClientIdStore>()));
        // Forward the interface to the same singleton so consumers can inject the
        // mockable IMultiHostClient surface; the concrete registration above owns
        // the lifetime (and async disposal), so this MUST resolve the SAME instance
        // rather than construct a second runtime.
        services.TryAddSingleton<IMultiHostClient>(sp => sp.GetRequiredService<MultiHostClient>());
        services.TryAddSingleton<IAhpClientFactory, AhpClientFactory>();

        // Register the options infrastructure unconditionally so IOptions<ClientConfig>
        // always resolves (with defaults when no configuration is supplied); layer the
        // caller's configuration on top when provided.
        var clientOptions = services.AddOptions<ClientConfig>();
        if (configureClient is not null)
            clientOptions.Configure(configureClient);

        return services;
    }

    /// <summary>
    /// The instrumentation-scope name to pass to OpenTelemetry's
    /// <c>AddSource(...)</c> (traces) and <c>AddMeter(...)</c> (metrics) so the AHP
    /// client's spans + metrics flow to your exporters. Equal to
    /// <see cref="AhpTelemetry.Name"/> / <see cref="AhpTelemetryNames.Source"/>.
    /// </summary>
    /// <remarks>
    /// This library intentionally takes no OpenTelemetry dependency — it originates
    /// only BCL <see cref="System.Diagnostics.ActivitySource"/> +
    /// <see cref="System.Diagnostics.Metrics.Meter"/> instruments, which are
    /// near-zero-cost when nothing is listening. Wire it from your composition root:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(t => t.AddSource(AhpServiceCollectionExtensions.TelemetrySourceName))
    ///     .WithMetrics(m => m.AddMeter(AhpServiceCollectionExtensions.TelemetrySourceName));
    /// </code>
    /// Naming the source by this constant (rather than re-typing the string) keeps
    /// the consumer's OTel pipeline in lock-step with the generated contract.
    /// </remarks>
    public const string TelemetrySourceName = AhpTelemetry.Name;
}

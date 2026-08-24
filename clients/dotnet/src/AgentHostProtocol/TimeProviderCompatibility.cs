#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

internal static class TimeProviderCompatibility
{
    public static Task DelayAsync(
        TimeProvider timeProvider,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        return Task.Delay(delay, timeProvider, cancellationToken);
#else
        return timeProvider.Delay(delay, cancellationToken);
#endif
    }

    public static CancellationTokenSource CreateCancellationTokenSource(
        TimeProvider timeProvider,
        TimeSpan delay)
    {
#if NET8_0_OR_GREATER
        return new CancellationTokenSource(delay, timeProvider);
#else
        return timeProvider.CreateCancellationTokenSource(delay);
#endif
    }
}

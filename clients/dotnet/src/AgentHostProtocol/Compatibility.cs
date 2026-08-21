#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

internal static class Compatibility
{
    public static TimeSpan GetElapsedTime(long startingTimestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - startingTimestamp;
        return TimeSpan.FromSeconds((double)elapsed / Stopwatch.Frequency);
    }

    public static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}

internal static class TaskCompatibilityExtensions
{
    public static async Task WaitAsync(this Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            cancellation))
        {
            Task completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
    }

    public static async Task<T> WaitAsync<T>(
        this Task<T> task,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            cancellation))
        {
            Task completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
            if (completed != task)
            {
                await completed.ConfigureAwait(false);
            }

            return await task.ConfigureAwait(false);
        }
    }

    public static async IAsyncEnumerable<T> ReadAllAsync<T>(
        this ChannelReader<T> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out T? item))
            {
                yield return item;
            }
        }
    }
}

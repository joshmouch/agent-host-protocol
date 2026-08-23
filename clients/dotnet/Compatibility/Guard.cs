#nullable enable

using System;

internal static class Guard
{
    public static void ThrowIfNull(object? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }
    }

    public static void ThrowIfDisposed(bool condition, object instance)
    {
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(condition, instance);
#else
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
#endif
    }
}

// Stable identifier for a host registered with MultiHostClient.
#nullable enable

using System;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>Opaque, stable identifier for a host registered with <see cref="MultiHostClient"/>.</summary>
public sealed class HostId : IEquatable<HostId>
{
    private readonly string _value;

    /// <summary>Creates a host ID from a string. The empty string is invalid.</summary>
    public HostId(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("HostId must not be empty.", nameof(value));
        _value = value;
    }

    /// <inheritdoc />
    public override string ToString() => _value;

    /// <inheritdoc />
    public bool Equals(HostId? other) => other is not null && _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HostId h && Equals(h);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator HostId(string s) => new(s);
}

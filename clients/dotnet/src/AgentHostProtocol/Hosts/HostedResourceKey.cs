// Stable (host, resource-URI) identity key. Mirrors Go's HostedResourceKey
// struct shape, plus a canonical percent-escaped string form so a host id and a
// resource URI compose into ONE collision-free key (a URI containing reserved
// characters like ':' '/' '?' can't be confused with the host/URI delimiter).
#nullable enable

using System;
using System.Text;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// Identifies a resource on a particular host. Value type with value equality,
/// mirroring Go's <c>HostedResourceKey</c>. <see cref="ToStableKey"/> yields a
/// canonical string in which the URI component is percent-escaped per RFC 3986
/// (unreserved characters pass through; everything else is %-escaped), so the
/// composed key is unambiguous.
/// </summary>
public readonly struct HostedResourceKey : IEquatable<HostedResourceKey>
{
    /// <summary>The host this resource belongs to.</summary>
    public HostId HostId { get; }

    /// <summary>The resource URI (unescaped, as the protocol uses it).</summary>
    public string Uri { get; }

    /// <summary>Creates a key from a host and a resource URI.</summary>
    public HostedResourceKey(HostId hostId, string uri)
    {
        HostId = hostId ?? throw new ArgumentNullException(nameof(hostId));
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
    }

    /// <summary>
    /// RFC 3986 unreserved set: ALPHA / DIGIT / '-' / '.' / '_' / '~'. These pass
    /// through <see cref="ToStableKey"/> unescaped; every other byte is %-escaped.
    /// </summary>
    private static bool IsUnreserved(char c) =>
        (c >= 'A' && c <= 'Z') ||
        (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') ||
        c == '-' || c == '.' || c == '_' || c == '~';

    /// <summary>
    /// Percent-escapes <paramref name="value"/> per RFC 3986 (UTF-8 bytes; uppercase
    /// hex digits, matching the RFC's normalized form).
    /// </summary>
    public static string PercentEscape(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var sb = new StringBuilder(value.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (IsUnreserved(c)) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The canonical key: the host id, a delimiter, and the percent-escaped URI.
    /// Because the URI is escaped, the delimiter can never appear inside it.
    /// </summary>
    public string ToStableKey() => $"{HostId} {PercentEscape(Uri)}";

    /// <inheritdoc />
    public bool Equals(HostedResourceKey other) =>
        // Null-safe on HostId so a default(HostedResourceKey) compares cleanly
        // (HostId is a reference type and is null on the default struct value).
        Equals(HostId, other.HostId) && string.Equals(Uri, other.Uri, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HostedResourceKey k && Equals(k);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(HostId, Uri);

    /// <inheritdoc />
    public override string ToString() => ToStableKey();
}

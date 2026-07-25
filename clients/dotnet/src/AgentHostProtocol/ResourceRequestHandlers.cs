// Typed per-method handlers for inbound server-initiated `resource*` requests.
// The typed layer over AhpClient.SetServerRequestHandler: install with
// AhpClient.SetResourceRequestHandlers. A method whose handler is left null is
// reported back to the peer as JSON-RPC MethodNotFound. Mirrors the TypeScript
// client's ResourceRequestHandlers (and Go's per-method On* handler struct).
#nullable enable

using System;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol;

/// <summary>
/// Typed per-method handlers for inbound server-initiated <c>resource*</c>
/// requests. Install with <see cref="AhpClient.SetResourceRequestHandlers"/>;
/// each handler receives the decoded params and returns (or resolves to) the
/// matching result. A method whose handler is <see langword="null"/> is reported
/// to the peer as JSON-RPC <c>MethodNotFound</c>.
/// </summary>
public sealed class ResourceRequestHandlers
{
    /// <summary>Handles an inbound <c>resourceRead</c> request.</summary>
    public Func<ResourceReadParams, Task<ResourceReadResult>>? OnResourceRead { get; init; }

    /// <summary>Handles an inbound <c>resourceWrite</c> request.</summary>
    public Func<ResourceWriteParams, Task<ResourceWriteResult>>? OnResourceWrite { get; init; }

    /// <summary>Handles an inbound <c>resourceList</c> request.</summary>
    public Func<ResourceListParams, Task<ResourceListResult>>? OnResourceList { get; init; }

    /// <summary>Handles an inbound <c>resourceCopy</c> request.</summary>
    public Func<ResourceCopyParams, Task<ResourceCopyResult>>? OnResourceCopy { get; init; }

    /// <summary>Handles an inbound <c>resourceDelete</c> request.</summary>
    public Func<ResourceDeleteParams, Task<ResourceDeleteResult>>? OnResourceDelete { get; init; }

    /// <summary>Handles an inbound <c>resourceMove</c> request.</summary>
    public Func<ResourceMoveParams, Task<ResourceMoveResult>>? OnResourceMove { get; init; }

    /// <summary>Handles an inbound <c>resourceResolve</c> request.</summary>
    public Func<ResourceResolveParams, Task<ResourceResolveResult>>? OnResourceResolve { get; init; }

    /// <summary>Handles an inbound <c>resourceMkdir</c> request.</summary>
    public Func<ResourceMkdirParams, Task<ResourceMkdirResult>>? OnResourceMkdir { get; init; }

    /// <summary>Handles an inbound <c>resourceRequest</c> request.</summary>
    public Func<ResourceRequestParams, Task<ResourceRequestResult>>? OnResourceRequest { get; init; }

    /// <summary>Handles an inbound <c>createResourceWatch</c> request.</summary>
    public Func<CreateResourceWatchParams, Task<CreateResourceWatchResult>>? OnCreateResourceWatch { get; init; }
}

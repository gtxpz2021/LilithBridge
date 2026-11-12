using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

// Ephemeral optional input to one existing shared-object request. This is not a
// dialogue/retention owner: it has no receipts, exposures or write transaction.
internal sealed class SharedActivityB1Supply
{
    private readonly MemoryStore _store;
    private readonly BridgeConfig _config;
    private readonly string _query;
    private readonly B1CapabilityOwner? _capability;
    private readonly long _generation;
    private readonly long _readOnlyGeneration;
    private readonly CancellationToken _cancellation;
    private readonly B1Packet _packet;
    private int _invalidated;
    private Func<bool>? _semanticPermitted;
    internal string Material { get; }

    private SharedActivityB1Supply(MemoryStore store, BridgeConfig config, string query,
        B1Packet packet, string material, CancellationToken cancellation)
    {
        _store = store; _config = config; _query = query; _packet = packet;
        _capability = config.B1CapabilityOwner;
        _generation = config.B1CapabilityGeneration;
        _readOnlyGeneration = config.B1SharedReadOnlyCapabilityGeneration;
        _cancellation = cancellation;
        Material = material;
    }

    internal static SharedActivityB1Supply? Capture(MemoryStore? store, BridgeConfig config,
        string requestId, string query, CancellationToken cancellation, SemanticRecallResult? semantic = null)
    {
        if (store == null || !B1CapabilityOwner.SharedReadOnlyEnabled(config) || cancellation.IsCancellationRequested
            || config.B1CapabilityOwner?.Permits(config.B1CapabilityGeneration) == false
            || config.B1CapabilityOwner?.SharedReadOnlyPermits(config.B1SharedReadOnlyCapabilityGeneration) == false)
            return null;
        try
        {
            var packet = store.CaptureB1ReadOnly(requestId, query, Array.Empty<MemoryTurn>(), config, includeHistoricalProposals: false, readQueryPurpose: B1ReadQueryPurpose.SharedObjectContent, semantic: semantic);
            // An empty/no-hit pool does not add a new contract or inhibit the
            // ordinary shared-object request. Original history alone is not a view.
            if (packet.Availability != "ready" || !packet.Materials.Any(m => m.Ref.Kind == "b1")) return null;
            string material = B1Continuity.RenderReadOnly(packet);
            if (string.IsNullOrWhiteSpace(material)) return null;
            var supply = new SharedActivityB1Supply(store, config, query, packet, material, cancellation);
            return supply.Permitted() ? supply : null;
        }
        catch (Exception)
        {
            // Optional read failure never creates a new request or write path.
            return null;
        }
    }

    internal static async Task<SharedActivityB1Supply?> CaptureAsync(MemoryStore? store, BridgeConfig config,
        SharedActivityRuntime runtime, SharedAttempt attempt)
    {
        if (store == null || !runtime.IsCurrent(attempt)) return null;
        var capability = config.B1CapabilityOwner;
        long generation = config.B1CapabilityGeneration, readGeneration = config.B1SharedReadOnlyCapabilityGeneration;
        string root = store.SemanticDataRoot, session = store.ReviewSessionIdentity, query = attempt.Snapshot.Content;
        var discover = store.SemanticDiscovery;
        var indexCurrent = store.SemanticIndexCurrent;
        bool CapabilityCurrent() => ReferenceEquals(capability, config.B1CapabilityOwner)
            && config.B1CapabilityGeneration == generation && config.B1SharedReadOnlyCapabilityGeneration == readGeneration
            && B1CapabilityOwner.SharedReadOnlyEnabled(config)
            && capability?.Permits(generation) != false && capability?.SharedReadOnlyPermits(readGeneration) != false;
        bool OwnerCurrent() => runtime.IsCurrent(attempt) && root == store.SemanticDataRoot
            && session == store.ReviewSessionIdentity && CapabilityCurrent();
        if (!CapabilityCurrent()) return null;
        SemanticRecallResult? semantic = null;
        if (config.SemanticRecallEnabled && discover != null && indexCurrent != null)
        {
            // This deadline starts with the real operation, not each await/retry.
            var deadline = attempt.CreatedAt.AddMilliseconds(config.SemanticQueryTimeoutMs);
            semantic = await store.DiscoverSemanticAsync(attempt.Id, attempt.Generation, query,
                SemanticDomain.B1, deadline, attempt.Token).ConfigureAwait(false);
            if (!OwnerCurrent()) throw new OperationCanceledException(attempt.Token);
            if (!config.SemanticRecallEnabled || !ReferenceEquals(discover, store.SemanticDiscovery)
                || !ReferenceEquals(indexCurrent, store.SemanticIndexCurrent)) throw new OperationCanceledException(attempt.Token);
            if (DateTimeOffset.UtcNow >= deadline || semantic.Unavailable.Length > 0) semantic = null;
            else if (semantic.QueryId != attempt.Id || semantic.Generation != attempt.Generation
                || semantic.QueryHash != MemoryStore.SemanticTextHash(query) || semantic.DataRoot != root
                || semantic.SessionId != session || semantic.Domains != SemanticDomain.B1
                || semantic.ModelRevision != SemanticRecallWorker.ModelRevision || !indexCurrent(semantic.IndexVersion))
                throw new OperationCanceledException(attempt.Token);
        }
        if (!OwnerCurrent()) throw new OperationCanceledException(attempt.Token);
        var supply = Capture(store, config, attempt.Id, query, attempt.Token, semantic);
        if (supply != null)
        {
            string? index = semantic?.IndexVersion;
            supply._semanticPermitted = () => OwnerCurrent() && (index == null || config.SemanticRecallEnabled
                && ReferenceEquals(discover, store.SemanticDiscovery) && ReferenceEquals(indexCurrent, store.SemanticIndexCurrent)
                && indexCurrent!(index));
            if (!supply.Permitted()) throw new OperationCanceledException(attempt.Token);
        }
        return supply;
    }

    internal bool Permitted()
    {
        // Once this exact supply has failed a live check it cannot revive if
        // a source is later restored. In particular, a silently cancelled
        // response must never become an empty fallback at display handoff.
        if (Volatile.Read(ref _invalidated) != 0) return false;
        try
        {
            bool permitted = (_semanticPermitted?.Invoke() ?? true) && !_cancellation.IsCancellationRequested && B1CapabilityOwner.SharedReadOnlyEnabled(_config)
                && _capability?.Permits(_generation) != false
                && _capability?.SharedReadOnlyPermits(_readOnlyGeneration) != false
                && _store.B1ReadOnlyPacketPermitted(_packet, _query, _config, B1ReadQueryPurpose.SharedObjectContent);
            if (!permitted) Interlocked.Exchange(ref _invalidated, 1);
            return permitted && Volatile.Read(ref _invalidated) == 0;
        }
        catch (Exception) { Interlocked.Exchange(ref _invalidated, 1); return false; }
    }
}

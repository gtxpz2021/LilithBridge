using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LilithBridge;

// Ephemeral optional input to one existing proactive MAIN. This is not a
// dialogue/retention owner: it has no receipts, exposures or write transaction.
internal sealed class ProactiveB1Supply
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
    internal string Material { get; }

    private ProactiveB1Supply(MemoryStore store, BridgeConfig config, string query,
        B1Packet packet, string material, CancellationToken cancellation)
    {
        _store = store; _config = config; _query = query; _packet = packet;
        _capability = config.B1CapabilityOwner;
        _generation = config.B1CapabilityGeneration;
        _readOnlyGeneration = config.B1ReadOnlyCapabilityGeneration;
        _cancellation = cancellation;
        Material = material;
    }

    internal static ProactiveB1Supply? Capture(MemoryStore? store, BridgeConfig config,
        string requestId, string query, IEnumerable<MemoryTurn> selectedHistory, CancellationToken cancellation)
    {
        if (store == null || !B1CapabilityOwner.ReadOnlyEnabled(config) || cancellation.IsCancellationRequested
            || config.B1CapabilityOwner?.Permits(config.B1CapabilityGeneration) == false
            || config.B1CapabilityOwner?.ReadOnlyPermits(config.B1ReadOnlyCapabilityGeneration) == false)
            return null;
        try
        {
            var packet = store.CaptureB1ReadOnly(requestId, query, selectedHistory, config);
            // An empty/no-hit pool does not add a new contract or inhibit the
            // ordinary proactive request. Original history alone is not a view.
            if (packet.Availability != "ready" || !packet.Materials.Any(m => m.Ref.Kind == "b1")) return null;
            string material = B1Continuity.RenderReadOnly(packet);
            if (string.IsNullOrWhiteSpace(material)) return null;
            var supply = new ProactiveB1Supply(store, config, query, packet, material, cancellation);
            return supply.Permitted() ? supply : null;
        }
        catch (Exception)
        {
            // Optional read failure never creates a new request or write path.
            return null;
        }
    }

    internal bool Permitted()
    {
        // Once this exact supply has failed a live check it cannot revive if
        // a source is later restored. In particular, a silently cancelled
        // response must never become an empty fallback at display handoff.
        if (Volatile.Read(ref _invalidated) != 0) return false;
        try
        {
            bool permitted = !_cancellation.IsCancellationRequested && B1CapabilityOwner.ReadOnlyEnabled(_config)
                && _capability?.Permits(_generation) != false
                && _capability?.ReadOnlyPermits(_readOnlyGeneration) != false
                && _store.B1ReadOnlyPacketPermitted(_packet, _query, _config);
            if (!permitted) Interlocked.Exchange(ref _invalidated, 1);
            return permitted && Volatile.Read(ref _invalidated) == 0;
        }
        catch (Exception) { Interlocked.Exchange(ref _invalidated, 1); return false; }
    }
}

using System;
using System.Linq;
using System.Threading;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private ReviewToolOutcome ExecuteLogicalMessageRead(BridgeConfig config, DialogueRequest request,
        ReviewToolArgs args, MemoryStore owner, CancellationToken token, MethodSource[] derived, MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if(args.Traversal != null)
            return ExecuteExperienceTraversal(config,request,args,owner,token,derived,offer);
        if(args.Message?.Reference.StartsWith("generated/",StringComparison.Ordinal)==true)
            return ExecuteGeneratedReplyRead(config,request,args,owner,token,derived,offer);
        if (!request.ReviewToolEnabled || !ReferenceEquals(request.ReviewStore, owner)
            || request.ReviewSessionIdentity != owner.ReviewSessionIdentity || request.ReviewDataRoot != DataPaths.Root()
            || request.ReviewConfigRoot != config.DataDirectory)
            return ReviewProcess.Empty("unavailable", "message_owner_mismatch");
        if (args.Message == null || args.SourceRefs.Count != 0 || args.IncludePriorThought)
            return ReviewProcess.Empty("invalid_arguments", "message_requires_its_own_locator_and_no_raw_refs_or_prior_thought");
        // Model-supplied question/query is not new permission to revisit a
        // restricted source. Use the owner's actual current utterance scope.
        var packet = owner.ReadLogicalMessage(request.ReviewCurrentText, config, args.Message);
        token.ThrowIfCancellationRequested();
        if (packet.Status != "ok") return new(packet.Json, 0, 0, 0, 0, 0, 0, false, LogicalMessage: packet);
        var snapshot = new ReviewSourceSnapshot(config, request, owner, args.Question,
            Array.Empty<MemoryTurn>(), derived, offer, logicalMessage: packet);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "source_changed");
        // Cross-session message reading does not register writable current-session
        // u:/a: aliases or turn its locating ID into a B1 premise.
        return new(packet.Json, 0, 0, 0, 0, 0, 0, false,
            SourceSnapshot: snapshot, ReadDataVersion: MemoryStore.SemanticTextHash(packet.Json),
            LogicalMessage: packet, MethodSourcesTracked: false);
    }
}

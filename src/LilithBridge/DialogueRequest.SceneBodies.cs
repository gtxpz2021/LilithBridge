using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal sealed record SceneBodyReferenceView(string Block, IReadOnlyList<RequestSourceBody> References);

internal sealed partial class DialogueRequest
{
    // Transient plan evidence only, never written to B1/experience or a save.
    private readonly List<SceneBodyReferenceView> _sceneBodyViews = new();
    internal void BindSceneBodyReferences(string block, IReadOnlyList<RequestSourceBody> references)
    {
        if (block.Length == 0 || references.Count == 0) return;
        lock (_b1Ownership)
        {
            if (_sceneBodyViews.Any(v => v.Block == block)) return;
            _sceneBodyViews.Add(new(block, references.ToArray()));
        }
    }

    internal IReadOnlyList<RequestSourceBody> SceneBodyReferencesFor(string context)
    {
        lock (_b1Ownership)
            return _sceneBodyViews.Where(v => context.Contains(v.Block, StringComparison.Ordinal))
                .SelectMany(v => v.References).Distinct().ToArray();
    }
}

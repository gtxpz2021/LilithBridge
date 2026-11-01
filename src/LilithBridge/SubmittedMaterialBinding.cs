namespace LilithBridge;

// Final binding after the client may have refreshed its request materials.
internal static class SubmittedMaterialBinding
{
    internal static void Apply(ApiResult result, DialogueRequest? request,
        PaperSourceSnapshot initialSources, NativeMemoTarget? initialTarget)
    {
        result.PaperSources = request?.SemanticMaterial?.PaperSourcesForReply(initialSources) ?? initialSources;
        // A present request owns its final target, including an intentional null.
        result.MemoAnnotationTarget = request == null ? initialTarget : request.MemoAnnotationTarget;
    }
}

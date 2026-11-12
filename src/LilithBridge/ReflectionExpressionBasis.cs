namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static void BindDispatchedReflectionExpressionBasis(DialogueRequest owner,ApiResult envelope)
    {
        var footprint=envelope.PublicExpressionFootprint;
        if(footprint==null||footprint.GeneratedSources.Length==0)return;
        var basis=owner.ReflectionReasonBasis;
        var generated=(basis?.GeneratedSources??Array.Empty<PublicExpressionGeneratedSource>()).Concat(footprint.GeneratedSources).Distinct().ToArray();
        // Provenance only: generated expressions never become raw target IDs,
        // player evidence, writable refs, or a new request for reflection.
        owner.ReflectionReasonBasis=new((basis?.Dependencies??Array.Empty<MethodSource>()).Concat(footprint.Sources).Distinct().ToArray(),
            basis?.Complete==true&&footprint.Complete,generated);
    }
}

internal sealed partial class MemoryStore
{
    private bool ReflectionExpressionSourcesCurrent(PublicExpressionGeneratedSource[]? dependencies)
    {
        if(dependencies==null)return true;
        if(dependencies.Length>192||dependencies.Any(d=>d==null))return false;
        foreach(var dependency in dependencies)
        {
            var source=ReadGeneratedReply(dependency.Key);
            if(source==null||source.BodyHash!=dependency.BodyHash||PublicExpressionImmutableIdentity(source)!=dependency.ImmutableIdentity)return false;
        }
        return true;
    }
}

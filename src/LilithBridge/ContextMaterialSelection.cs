using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

/// <summary>
/// The single request-time material decision for one ContextBuilder.Build call:
/// which story material is supplied (identity core / framed full archive /
/// query-targeted archive recall), whether her current relationship reading is
/// supplied, and why. It only selects and frames already-owned material; it
/// writes nothing and changes no lifecycle. All three assembly branches
/// (legacy / clean / attributionIsolation) consume the same instance, and the
/// result is serialized next to the request diagnostics.
/// </summary>
internal sealed class ContextMaterialSelection
{
    internal const string SchemaKind = "context_material_selection";
    internal const int SchemaVersion = 1;
    internal const string DisabledMode = "disabled";
    internal const string IdentityCoreMode = "identity_core";
    internal const string FullArchiveMode = "full_archive";
    internal const string TargetedRecallMode = "targeted_recall";
    internal const string FullSupplyScope = "full";
    internal const string AttributionMinimalScope = "attribution_minimal";
    internal const string AttributionMinimalReason = "attribution_branch_minimal";
    // E5 (15028): a selected relationship reading is only "supplied" when this
    // request actually renders it. The reason names which render path owned it.
    internal const string PerspectiveProjectionSupplyReason = "perspective_projection";
    internal const string SelfPositionViewSupplyReason = "self_position_view";
    internal const string PerspectiveProjectionDisabledReason = "perspective_projection_disabled";
    internal const string NoRelationshipCandidateReason = "no_relationship_candidate";
    internal const string SelectionUnavailableReason = "selection_unavailable";

    /// <summary>Which story material this request carries. Empty block = nothing.</summary>
    internal sealed record StoryMaterial(
        string Mode, string Reason, string Term, string Block, string RecallQuery, int RecallChars)
    {
        internal static StoryMaterial Disabled(string reason) =>
            new(ContextMaterialSelection.DisabledMode, reason, "", "", "", 0);
    }

    /// <summary>One serialized inclusion/exclusion row of the selection.</summary>
    internal sealed record MaterialEntry(
        string Kind, string EventId, string Reason, bool Included, string Source, string Query);

    internal string Branch { get; private init; } = "";
    internal string Query { get; private init; } = "";
    internal string QuerySource { get; private init; } = "";
    internal StoryMaterial Story { get; private init; } = StoryMaterial.Disabled("not_evaluated");
    internal RelationshipMaterialSelection Relationship { get; private init; } =
        new(null, "not_evaluated", "", "memory_store.relationship_readings");
    internal IReadOnlyList<MaterialEntry> Included { get; private init; } = Array.Empty<MaterialEntry>();
    internal IReadOnlyList<MaterialEntry> Excluded { get; private init; } = Array.Empty<MaterialEntry>();

    /// <summary>
    /// What this request actually ships: "full" while the branch renders the
    /// selected material, "attribution_minimal" once the isolation branch has
    /// settled the same decision to candidate-only. Included/Excluded print the
    /// consequence; the candidate fields keep the decision that was computed.
    /// </summary>
    internal string SupplyScope { get; private init; } = FullSupplyScope;
    internal bool StorySupplied => SupplyScope == FullSupplyScope && Story.Block.Length > 0;
    /// <summary>
    /// True only when the selected relationship reading is actually rendered into
    /// this request. Candidate existence alone is not supply: with
    /// PerspectiveProjectionEnabled off and no self-position view the reading is
    /// a candidate only (E5, 15028).
    /// </summary>
    internal bool RelationshipSupplied => SupplyScope == FullSupplyScope && RelationshipRendered;
    /// <summary>Whether this request's rendering path actually prints the reading.</summary>
    internal bool RelationshipRendered { get; private init; }
    /// <summary>Why the reading is supplied or not; serialized as diagnostics.</summary>
    internal string RelationshipSupplyReason { get; private init; } = NoRelationshipCandidateReason;

    /// <summary>The identity core this decision supplies, if that is the chosen story mode.</summary>
    internal string StoryIdentityCore => Story.Mode == IdentityCoreMode ? Story.Block : "";

    /// <summary>
    /// Settle the one decision for a branch that deliberately renders neither the
    /// relationship reading nor the story material. Candidate selection stays
    /// recorded (mode/reason/identity describe what was chosen), but every row
    /// that would have been "included" becomes an excluded row carrying the
    /// branch reason. Nothing is forced into the isolated request to satisfy a
    /// field: the diagnostic now says "candidate only", which is what ships.
    /// </summary>
    internal ContextMaterialSelection WithoutMaterialSupply()
    {
        if (SupplyScope == AttributionMinimalScope)
            return this;
        var excluded = new List<MaterialEntry>(Excluded);
        foreach (MaterialEntry entry in Included)
            excluded.Add(entry with { Included = false, Reason = AttributionMinimalReason });
        return new ContextMaterialSelection
        {
            Branch = Branch,
            Query = Query,
            QuerySource = QuerySource,
            Story = Story,
            Relationship = Relationship,
            Included = Array.Empty<MaterialEntry>(),
            Excluded = excluded,
            SupplyScope = AttributionMinimalScope,
            // The isolation branch renders neither material kind: the decision
            // stays recorded in Relationship, but nothing is supplied.
            RelationshipRendered = false,
            RelationshipSupplyReason = AttributionMinimalReason
        };
    }

    private ContextMaterialSelection()
    {
    }

    internal static ContextMaterialSelection Unavailable(string branch, string query, string querySource) => new()
    {
        Branch = branch,
        Query = query ?? "",
        QuerySource = querySource,
        Story = StoryMaterial.Disabled("selection_unavailable"),
        Relationship = new RelationshipMaterialSelection(null, "selection_unavailable", query ?? "",
            "memory_store.relationship_readings"),
        RelationshipSupplyReason = SelectionUnavailableReason
    };

    /// <summary>
    /// Computes the one decision this request will consume. Defensive: material
    /// selection is optional context, so a failing lookup becomes "nothing
    /// selected" with a recorded reason instead of breaking the request.
    /// </summary>
    internal static ContextMaterialSelection Select(MemoryStore memory, BridgeConfig config,
        string query, string querySource, string branch, string? previousUserLine = null)
    {
        string text = query ?? "";
        if (memory == null || config == null)
            return Unavailable(branch, text, querySource);
        RelationshipMaterialSelection relationship;
        try
        {
            relationship = memory.SelectRequestRelationshipMaterial(text);
        }
        catch
        {
            relationship = new RelationshipMaterialSelection(null, "selection_failed", text,
                "memory_store.relationship_readings");
        }
        StoryMaterial story = SelectStory(config, text);
        var included = new List<MaterialEntry>();
        var excluded = new List<MaterialEntry>();
        if (story.Mode == DisabledMode)
        {
            excluded.Add(new MaterialEntry("story_material", "", story.Reason, false,
                "previous_story_archive", story.Term));
        }
        else
        {
            included.Add(new MaterialEntry("story_material", "", story.Reason, true,
                story.Mode == TargetedRecallMode ? "previous_story_archive.recall" : "previous_story_archive",
                story.Mode == TargetedRecallMode ? story.RecallQuery : story.Term));
            if (story.Mode == FullArchiveMode)
                excluded.Add(new MaterialEntry("story_material", "", "auto_recall_not_used", false,
                    "previous_story_archive.recall", story.Term));
            else if (story.Mode == TargetedRecallMode)
                excluded.Add(new MaterialEntry("story_material", "", "full_archive_replaced_by_recall", false,
                    "previous_story_archive", story.Term));
            else
                excluded.Add(new MaterialEntry("story_material", "",
                    story.Reason == "ordinary_turn" ? "no_story_term" : story.Reason, false,
                    "previous_story_archive", ""));
        }
        // E5: settle the relationship row against the paths that actually render
        // it in this request. The candidate decision stays recorded in
        // Relationship/relationship_selected; only the supplied flag and the row
        // follow what the request really ships.
        bool relationshipRendered;
        string relationshipSupplyReason;
        if (relationship.Turn == null)
        {
            relationshipRendered = false;
            relationshipSupplyReason = NoRelationshipCandidateReason;
        }
        else if (config.PerspectiveProjectionEnabled)
        {
            // The composer block prints the selected reading whenever PP is on.
            relationshipRendered = true;
            relationshipSupplyReason = PerspectiveProjectionSupplyReason;
        }
        else if (config.SelfPositionViewEnabled
            && SelfPositionGate.ShouldSupply(text, previousUserLine))
        {
            // PP is off, but this request's self-position view renders the same
            // reading in its shared_authorship field, so it does ship.
            relationshipRendered = true;
            relationshipSupplyReason = SelfPositionViewSupplyReason;
        }
        else
        {
            relationshipRendered = false;
            relationshipSupplyReason = PerspectiveProjectionDisabledReason;
        }
        if (relationship.Turn != null && relationshipRendered)
        {
            included.Add(new MaterialEntry("relationship_reading", relationship.EventId,
                relationship.Reason, true, relationship.Source, text));
        }
        else if (relationship.Turn != null)
        {
            excluded.Add(new MaterialEntry("relationship_reading", relationship.EventId,
                relationshipSupplyReason, false, relationship.Source, text));
        }
        else
        {
            excluded.Add(new MaterialEntry("relationship_reading", "", relationship.Reason, false,
                relationship.Source, text));
        }
        return new ContextMaterialSelection
        {
            Branch = branch,
            Query = text,
            QuerySource = querySource,
            Story = story,
            Relationship = relationship,
            Included = included,
            Excluded = excluded,
            RelationshipRendered = relationshipRendered,
            RelationshipSupplyReason = relationshipSupplyReason
        };
    }

    private static StoryMaterial SelectStory(BridgeConfig config, string text)
    {
        if (!config.PreviousStoryEnabled && !config.PreviousStoryAutoRecallEnabled)
            return StoryMaterial.Disabled("previous_story_disabled");
        if (!PreviousStoryArchive.IsAvailable)
            return StoryMaterial.Disabled("previous_story_unavailable");
        string? term = PreviousStoryArchive.FindStoryTerm(text);
        if (term != null)
        {
            // The archive auto-recall used to be appended by the controller after
            // Build. It now travels inside this decision, so a request carries one
            // story material instead of the archive block plus a second recall.
            if (config.PreviousStoryAutoRecallEnabled)
            {
                try
                {
                    StoryRecallResult? recall = PreviousStoryArchive.AutoRecall(text, "", config);
                    if (recall is { Found: true })
                    {
                        string block = PreviousStoryArchive.BuildRecallContext(recall, config);
                        if (block.Length > 0)
                        {
                            NativeEventLog.Trace("story.recall.auto", "mod", "system",
                                "automatic story-archive recall selected by material selection",
                                "query=" + recall.Query + ";chars=" + recall.Content.Length
                                + ";source=context_builder.build");
                            return new StoryMaterial(TargetedRecallMode, "story_term:" + term, term,
                                block, recall.Query, block.Length);
                        }
                    }
                }
                catch
                {
                    // Story archive must never break a request.
                }
            }
            if (config.PreviousStoryEnabled)
            {
                try
                {
                    string framed = PreviousStoryArchive.FrameAsPartialRecord(PreviousStoryArchive.Build(config));
                    if (framed.Length > 0)
                        return new StoryMaterial(FullArchiveMode, "story_term:" + term, term, framed, "", 0);
                    // No archive body available: keep the always-on identity core
                    // instead of sending navigation without any material.
                    string core = PreviousStoryArchive.BuildIdentityCore();
                    if (core.Length > 0)
                        return new StoryMaterial(IdentityCoreMode, "story_term_no_archive:" + term, term, core, "", 0);
                }
                catch
                {
                    // Story archive must never break a request.
                }
            }
            return StoryMaterial.Disabled(config.PreviousStoryAutoRecallEnabled
                ? "auto_recall_not_found_and_story_disabled"
                : "previous_story_disabled");
        }
        if (!config.PreviousStoryEnabled)
            return StoryMaterial.Disabled("previous_story_disabled");
        try
        {
            string core = PreviousStoryArchive.BuildIdentityCore();
            string? common = PreviousStoryArchive.FindCommonHistoryTerm(text);
            // Common-history words ask for the memory/experience path. They are
            // recorded so the trace can prove the archive was deliberately kept
            // closed, not merely missing.
            return new StoryMaterial(IdentityCoreMode,
                common == null ? "ordinary_turn" : "common_history_term:" + common, "", core, "", 0);
        }
        catch
        {
            return StoryMaterial.Disabled("identity_core_unavailable");
        }
    }

    /// <summary>Serializable decision for the request dump and the harness.</summary>
    internal string ToJson() => JsonSerializer.Serialize(new
    {
        kind = SchemaKind,
        schema_version = SchemaVersion,
        branch = Branch,
        query = Query,
        query_source = QuerySource,
        event_id = Relationship.EventId,
        reason = PrimaryReason(),
        story_mode = Story.Mode,
        story_reason = Story.Reason,
        story_term = Story.Term,
        story_chars = Story.Block.Length,
        story_recall_query = Story.RecallQuery,
        story_recall_chars = Story.RecallChars,
        supply_scope = SupplyScope,
        story_supplied = StorySupplied,
        relationship_supplied = RelationshipSupplied,
        relationship_supply_reason = RelationshipSupplyReason,
        relationship_selected = Relationship.Turn != null,
        relationship_event_id = Relationship.EventId,
        relationship_reason = Relationship.Reason,
        relationship_source = Relationship.Source,
        source = "context_builder.build",
        included = Included.Select(EntryJson).ToArray(),
        excluded = Excluded.Select(EntryJson).ToArray()
    }, DialogueMaterialEnvelope.DataJson);

    private string PrimaryReason()
    {
        if (SupplyScope == AttributionMinimalScope)
            return "attribution_branch_minimal:candidate_only";
        if (Relationship.Turn != null)
            return "relationship:" + Relationship.Reason;
        if (Story.Mode != DisabledMode)
            return "story:" + Story.Reason;
        return "ordinary_material";
    }

    private static object EntryJson(MaterialEntry entry) => new
    {
        kind = entry.Kind,
        event_id = entry.EventId,
        reason = entry.Reason,
        included = entry.Included,
        source = entry.Source,
        query = entry.Query
    };
}

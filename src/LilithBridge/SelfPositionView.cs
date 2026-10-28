using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LilithBridge;

/// <summary>
/// How a line of the view knows itself. The label comes from the owner material
/// the line quotes — the formatter never upgrades one into another, and a field
/// with no owner stays <see cref="Absent"/> instead of being filled in.
/// </summary>
internal static class SelfPositionStatus
{
    /// <summary>An archive or record keeper states this.</summary>
    public const string Recorded = "recorded";
    /// <summary>The program observed it this turn.</summary>
    public const string Observed = "observed";
    /// <summary>Her own reading: revisable, never a fact.</summary>
    public const string Interpretation = "interpretation";
    /// <summary>A rule or hard boundary already in force.</summary>
    public const string Constraint = "constraint";
    /// <summary>No owner supplied it this turn.</summary>
    public const string Absent = "absent";
}

internal sealed record SelfPositionField(string Name, string Status, string Text,
    IReadOnlyList<string> Sources);

/// <summary>
/// Request-time self-position material: recomputable from existing owners, it
/// writes nothing, and discarding it returns the request to its previous shape.
/// There is no global "updated_at" because the parts keep their own times.
/// </summary>
internal sealed record SelfPositionView(IReadOnlyList<SelfPositionField> Fields,
    DateTimeOffset AssembledAt);

/// <summary>
/// Plain values read from existing owners. The assembler below never receives a
/// store or a session object, so it cannot mutate an owner even by accident.
/// </summary>
internal sealed record SelfPositionInputs(
    bool StoryArchiveEnabled,
    string StoryIdentityCore,
    string NativeState,
    MemoryTurn? RelationshipReading,
    IReadOnlyList<MemoryTurn> VersionChain);

internal static class SelfPositionViewAssembler
{
    /// <summary>
    /// The only place that touches existing owners, and only through their read
    /// APIs. Missing owners stay missing: this method never invents a value.
    /// When the caller already computed the request-time material selection, its
    /// relationship reading and story identity core are passed in: this consumer
    /// and the composer then share one decision. A caller without a selection
    /// delegates to the same unified selector instead of a second heuristic.
    /// </summary>
    public static SelfPositionInputs Read(BridgeConfig config, SessionState state, MemoryStore memory,
        string? query = null, RelationshipMaterialSelection? relationship = null,
        string? storyIdentityCore = null)
    {
        var chain = new List<MemoryTurn>();
        MemoryTurn? current = memory.FilterMentionSupply(memory.GetReflectionNotes(1)).LastOrDefault();
        if (current != null)
            chain.Add(current);
        MemoryTurn? retired = memory.FilterMentionSupply(memory.GetSettledNotes(1).Concat(memory.GetSupersededNotes(1)))
            .OrderByDescending(t => t.SettledAt ?? t.Timestamp)
            .FirstOrDefault();
        if (retired != null)
            chain.Add(retired);
        MemoryTurn? reading = relationship != null
            ? relationship.Turn
            : memory.SelectRelationshipReading(query ?? state.LastUserText);
        string storyCore = storyIdentityCore
            ?? (config.PreviousStoryEnabled ? PreviousStoryArchive.BuildIdentityCore() : "");
        return new SelfPositionInputs(
            config.PreviousStoryEnabled,
            storyCore,
            state.Native.Describe(),
            reading,
            chain);
    }

    public static SelfPositionView Assemble(SelfPositionInputs inputs, DateTimeOffset assembledAt) =>
        new(new[]
        {
            Origin(inputs),
            CurrentWorld(inputs),
            SharedAuthorship(inputs),
            Revisability(),
            Continuity(inputs),
            BoundaryAwareness()
        }, assembledAt);

    private static SelfPositionField Origin(SelfPositionInputs inputs)
    {
        string text = Flatten(inputs.StoryIdentityCore);
        return text.Length == 0
            ? Absent("origin")
            : new SelfPositionField("origin", SelfPositionStatus.Recorded, text,
                new[] { "archive:identity_core" });
    }

    private static SelfPositionField CurrentWorld(SelfPositionInputs inputs)
    {
        string native = Flatten(inputs.NativeState);
        return native.Length == 0
            ? Absent("current_world")
            : new SelfPositionField("current_world", SelfPositionStatus.Observed,
                "你在玩家桌面上运行；程序此刻的原生状态：" + native,
                new[] { "native_state", "identity:desktop_lilith" });
    }

    private static SelfPositionField SharedAuthorship(SelfPositionInputs inputs)
    {
        MemoryTurn? reading = inputs.RelationshipReading;
        if (reading == null)
            return Absent("shared_authorship");
        var sources = new List<string> { "reading:" + reading.EventId };
        sources.AddRange(reading.SourceNoteIds.Select(id => "note:" + id));
        return new SelfPositionField("shared_authorship", SelfPositionStatus.Interpretation,
            "你与玩家的共同经历以经历档与她自己的关系理解为限；最近那条理解："
            + Flatten(reading.Text),
            sources);
    }

    private static SelfPositionField Revisability() =>
        new("revisability", SelfPositionStatus.Constraint,
            "这轮材料都能被玩家的明确记忆操作更正、收起或撤回（active/settled/superseded，别提/删除/更正）；本视图不绕过其中任何一条。",
            new[] { "memory_lifecycle", "persona:memory_operations" });

    private static SelfPositionField Continuity(SelfPositionInputs inputs)
    {
        if (inputs.VersionChain.Count == 0)
            return Absent("continuity");
        string chain = string.Join("；", inputs.VersionChain.Select(t =>
            "note=" + t.EventId + " state=" + State(t) + " revision=" + t.Revision));
        return new SelfPositionField("continuity", SelfPositionStatus.Recorded,
            "记录版本的沿革以链路为准：" + chain + "；这些状态说明记录生命周期，仅凭旧记录不能判断现在是否认同其内容。",
            inputs.VersionChain.Select(t => "note:" + t.EventId).ToList());
    }

    private static SelfPositionField BoundaryAwareness() =>
        new("boundary_awareness", SelfPositionStatus.Constraint,
            "边界以本轮给出的能力契约、原生状态与现实边界为准；本视图不新增、不放宽，也不把边界升级成自我认知。",
            new[] { "persona:reality_boundary", "capability:contract" });

    private static SelfPositionField Absent(string name) =>
        new(name, SelfPositionStatus.Absent, "", Array.Empty<string>());

    private static string State(MemoryTurn turn) =>
        string.IsNullOrWhiteSpace(turn.LifecycleState) ? "active" : turn.LifecycleState;

    private static string Flatten(string? text) =>
        string.Join(" ", (text ?? "").Replace("\r", "").Split('\n',
            StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("[", StringComparison.Ordinal))
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal)
                ? line[2..].TrimStart() : line));
}

/// <summary>
/// Organises wording only: every claim in the block comes from the field it is
/// printed under, and no line re-judges which interpretation is current.
/// </summary>
internal static class SelfPositionViewFormatter
{
    public const string Header =
        "[自我位置（本轮临时装配；每项都来自现有材料，可随时重算，不是新的记忆）]";
    public const string FooterPrefix = "assembled_at=";

    public static string Render(SelfPositionView view)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        foreach (SelfPositionField field in view.Fields)
        {
            builder.Append("- ").Append(field.Name)
                .Append("（status=").Append(field.Status)
                .Append("；sources=")
                .Append(field.Sources.Count == 0 ? "-" : string.Join(",", field.Sources))
                .Append("）");
            if (field.Text.Length > 0)
                builder.Append("：").Append(field.Text);
            builder.AppendLine();
        }
        builder.Append(FooterPrefix).Append(view.AssembledAt.ToString("O"))
            .AppendLine("；没有来源的项标为 absent，不要替它补内容；本视图不是新的记忆，也不改变任何边界。");
        return builder.ToString();
    }
}

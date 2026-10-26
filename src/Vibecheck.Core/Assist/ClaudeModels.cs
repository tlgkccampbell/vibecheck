namespace Vibecheck.Assist;

/// <summary>
/// Names the model each kind of request to Claude uses. The defaults are chosen per task for the best compromise of
/// quality, speed and cost, as measured in <c>docs/review-workflow.md</c> section 7, and pinned to exact model
/// identifiers, so that a new Claude Code release does not change what the tool produces.
/// </summary>
/// <param name="Map">The model that proposes and revises a concept map.</param>
/// <param name="Placement">The model that places the types a generated map leaves unsorted.</param>
/// <param name="Narrative">The model that writes the narratives of a changeset.</param>
/// <param name="Explainer">The model that writes the explainer of a changeset.</param>
public sealed record ClaudeModels(string Map, string Placement, string Narrative, string Explainer)
{
    /// <summary>
    /// Gets the default model for each kind of request. A map is generated once per solution and every later review
    /// draws with it, so it gets the strongest model. Placement is a small judgment that Sonnet makes as well as Opus,
    /// faster and for less. An explainer is read once and slowly, so it gets the model that writes the clearer one, as
    /// <c>docs/explainers.md</c> section 6 measured.
    /// </summary>
    public static ClaudeModels Default { get; } = new("claude-opus-5-5", "claude-sonnet-5-5", "claude-sonnet-5-5", "claude-opus-5-5");

    /// <summary>Returns the models that use one model, an alias such as <c>sonnet</c> or an exact identifier, for every kind of request.</summary>
    public static ClaudeModels All(string model) => new(model, model, model, model);
}

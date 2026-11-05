using Coanda.Core.Agents;

namespace Coanda.Tests;

/// <summary>
/// The software module's five agents, build, reviewer-adversarial, reviewer-conformance, fix, and
/// discovery, read from its definitions folder.
/// </summary>
public static class SoftwareAgents
{
    public static readonly AgentDefinition Build = AgentDefinition.FromJson(SoftwareDefinitions.Read("agents", "build"));

    public static readonly AgentDefinition ReviewerAdversarial = AgentDefinition.FromJson(SoftwareDefinitions.Read("agents", "reviewer-adversarial"));

    public static readonly AgentDefinition ReviewerConformance = AgentDefinition.FromJson(SoftwareDefinitions.Read("agents", "reviewer-conformance"));

    public static readonly AgentDefinition Fix = AgentDefinition.FromJson(SoftwareDefinitions.Read("agents", "fix"));

    public static readonly AgentDefinition Discovery = AgentDefinition.FromJson(SoftwareDefinitions.Read("agents", "discovery"));

    public static readonly IReadOnlyList<AgentDefinition> All = [Build, ReviewerAdversarial, ReviewerConformance, Fix, Discovery];

    /// <summary>The vendor the build agent's model names.</summary>
    public static string Vendor => Build.Model.Vendor;

    /// <summary>The sentence the build agent's rules open with, which sends the agent to the repository's AGENTS.md and CLAUDE.md.</summary>
    public static string ProjectGuidance => Build.Persona.Rules[..(Build.Persona.Rules.IndexOf(". ", StringComparison.Ordinal) + 1)];
}

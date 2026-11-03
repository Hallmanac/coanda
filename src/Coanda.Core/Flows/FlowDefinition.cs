using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coanda.Core.Flows;

/// <summary>
/// A flow as validated data: the parcel types it accepts, the outcome it declares, a budget and a
/// spawn cap, an optional follow, and an ordered list of named steps, the first of which is where a
/// flight starts. It routes on an answer field equalling a value and a visit count and never
/// computes. Validation takes the set of known action names as an input, so this type holds no
/// action catalog and nothing software-specific.
/// </summary>
public sealed record FlowDefinition
{
    /// <summary>The target that parks the flight.</summary>
    public const string Park = "park";

    /// <summary>The target that ends the flight with the outcome this flow declares.</summary>
    public const string End = "end";

    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Accepts { get; init; } = [];
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The most a flight may cost, in US dollars, the unit the measurement events record per spawn.</summary>
    public decimal Budget { get; init; }

    /// <summary>The most agents a flight may spawn.</summary>
    public int SpawnCap { get; init; }

    /// <summary>
    /// The outcome this flow follows, with its depth cap, or null when the flow starts only by a
    /// binding, a subscription, or a person. Left out of the content when null, so a flow without
    /// one reads and hashes as it did before follows existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowFollow? Follow { get; init; }

    public IReadOnlyList<FlowStep> Steps { get; init; } = [];

    /// <summary>Content hash of the flow, computed from its content alone.</summary>
    [JsonIgnore]
    public string Hash => ComputeHash();

    /// <summary>The step a flight starts at, or null when the flow has no steps.</summary>
    [JsonIgnore]
    public FlowStep? First => Steps.Count == 0 ? null : Steps[0];

    public FlowStep? Step(string name) => Steps.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Whether a name is plain: letters, digits, hyphens, and underscores, and not empty.</summary>
    public static bool IsPlainName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Refuses a flow that breaks a rule, with a <see cref="DomainException"/> quoting it.
    /// <paramref name="knownActions"/> is every action name the caller's catalogs define.
    /// </summary>
    public void Validate(IReadOnlySet<string> knownActions)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("A flow needs a name.");
        }

        if (Accepts.Count == 0 || Accepts.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainException($"Flow '{Name}' needs at least one accepted parcel type.");
        }

        if (string.IsNullOrWhiteSpace(Outcome))
        {
            throw new DomainException($"Flow '{Name}' needs an outcome.");
        }

        if (Budget <= 0)
        {
            throw new DomainException($"Flow '{Name}' needs a budget greater than zero; every flow declares one.");
        }

        if (SpawnCap <= 0)
        {
            throw new DomainException($"Flow '{Name}' needs a spawn cap greater than zero; every flow declares one.");
        }

        if (Follow is not null)
        {
            ValidateFollow(Follow);
        }

        if (Steps.Count == 0)
        {
            throw new DomainException($"Flow '{Name}' needs at least one step.");
        }

        ValidateStepNames();
        foreach (FlowStep step in Steps)
        {
            ValidateStep(step, knownActions);
        }
    }

    private void ValidateFollow(FlowFollow follow)
    {
        if (string.IsNullOrWhiteSpace(follow.Outcome))
        {
            throw new DomainException($"Flow '{Name}' follows an outcome, so its follow needs the outcome's name.");
        }

        if (follow.Flow is not null && !IsPlainName(follow.Flow))
        {
            throw new DomainException(
                $"Flow '{Name}' follows the flow '{follow.Flow}'; a followed flow's name is plain: letters, digits, hyphens, and underscores.");
        }

        if (follow.DepthCap <= 0)
        {
            throw new DomainException(
                $"Flow '{Name}' follows an outcome, so its follow needs a depth cap greater than zero; every composition declares one.");
        }
    }

    private void ValidateStepNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FlowStep step in Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                throw new DomainException($"Flow '{Name}' has a step with no name.");
            }

            if (step.Name.Equals(Park, StringComparison.OrdinalIgnoreCase) || step.Name.Equals(End, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException($"Flow '{Name}' has a step named '{step.Name}'; park and end are reserved targets and cannot name a step.");
            }

            if (!seen.Add(step.Name))
            {
                throw new DomainException($"Flow '{Name}' has two steps named '{step.Name}'; step names are unique.");
            }
        }
    }

    private void ValidateStep(FlowStep step, IReadOnlySet<string> knownActions)
    {
        bool hasAgent = !string.IsNullOrWhiteSpace(step.Agent);
        bool hasAction = !string.IsNullOrWhiteSpace(step.Action);
        bool hasSchema = !string.IsNullOrWhiteSpace(step.AnswerSchema);

        if (hasAgent == hasAction)
        {
            throw new DomainException(
                $"Step '{step.Name}' of flow '{Name}' must name an agent (a judgment) or an action, and exactly one of them.");
        }

        if (hasAgent && !hasSchema)
        {
            throw new DomainException($"Judgment step '{step.Name}' of flow '{Name}' needs an agent and an answer schema.");
        }

        if (!string.IsNullOrWhiteSpace(step.Action))
        {
            ValidateAction(step, step.Action, knownActions, hasSchema);
        }
        else if (step.Arguments.Count > 0)
        {
            throw new DomainException($"Judgment step '{step.Name}' of flow '{Name}' holds no arguments; only an action step does.");
        }

        if (step.Transitions.Count == 0)
        {
            throw new DomainException($"Step '{step.Name}' of flow '{Name}' needs at least one transition.");
        }

        foreach (FlowTransition transition in step.Transitions)
        {
            ValidateTransition(step, transition);
        }
    }

    private void ValidateAction(FlowStep step, string action, IReadOnlySet<string> knownActions, bool hasSchema)
    {
        if (hasSchema)
        {
            throw new DomainException($"Action step '{step.Name}' of flow '{Name}' holds no answer schema; only a judgment step does.");
        }

        if (!knownActions.Contains(action))
        {
            throw new DomainException($"Step '{step.Name}' of flow '{Name}' names the action '{action}', which is not a known action.");
        }

        foreach ((string argument, JsonElement value) in step.Arguments)
        {
            if (!IsPlainName(argument))
            {
                throw new DomainException(
                    $"Step '{step.Name}' of flow '{Name}' has an argument named '{argument}'; an argument name is plain: letters, digits, hyphens, and underscores.");
            }

            if (!FlowValue.IsAllowed(value))
            {
                throw new DomainException(
                    $"Argument '{argument}' of step '{step.Name}' in flow '{Name}' must be a JSON string, number, or boolean.");
            }
        }
    }

    private void ValidateTransition(FlowStep step, FlowTransition transition)
    {
        if (!IsPlainName(transition.Field))
        {
            throw new DomainException(
                $"A transition of step '{step.Name}' in flow '{Name}' has the answer field '{transition.Field}'; an answer field is plain: letters, digits, hyphens, and underscores.");
        }

        if (!FlowValue.IsAllowed(transition.Value))
        {
            throw new DomainException(
                $"The transition of step '{step.Name}' in flow '{Name}' on '{transition.Field}' needs a value that is a JSON string, number, or boolean.");
        }

        if (transition.VisitCap <= 0)
        {
            throw new DomainException(
                $"The transition of step '{step.Name}' in flow '{Name}' on '{transition.Field}' needs a visit cap greater than zero; every transition declares one.");
        }

        bool reachable = transition.Target is Park or End || Step(transition.Target) is not null;
        if (!reachable)
        {
            throw new DomainException(
                $"The transition of step '{step.Name}' in flow '{Name}' on '{transition.Field}' targets '{transition.Target}', which is not a step of the flow, park, or end.");
        }
    }

    private string ComputeHash()
    {
        var canonical = new
        {
            name = Name,
            accepts = Accepts,
            outcome = Outcome,
            budget = Budget,
            spawnCap = SpawnCap,
            steps = Steps.Select(step => new
            {
                name = step.Name,
                agent = step.Agent,
                answerSchema = step.AnswerSchema,
                action = step.Action,
                arguments = step.Arguments.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value),
                transitions = step.Transitions.Select(t => new { field = t.Field, value = t.Value, visitCap = t.VisitCap, target = t.Target }),
            }),
        };
        // A flow without a follow hashes exactly as it did before follows existed.
        JsonNode node = JsonSerializer.SerializeToNode(canonical)
            ?? throw new InvalidOperationException("A flow's canonical form serialized to nothing.");
        if (Follow is not null)
        {
            node["follow"] = JsonSerializer.SerializeToNode(new { outcome = Follow.Outcome, flow = Follow.Flow, depthCap = Follow.DepthCap });
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string CanonicalJson(FlowDefinition flow) => JsonSerializer.Serialize(flow, WriteOptions);

    /// <summary>
    /// Reads a flow from the JSON <see cref="CanonicalJson"/> writes. A property the shape does not
    /// define, such as a condition or an expression on a transition, is refused by name rather than
    /// ignored, so a definition can never carry logic the interpreter would not run.
    /// </summary>
    public static FlowDefinition FromJson(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<FlowDefinition>(content, ReadOptions)
                ?? throw new DomainException("The content is empty; it cannot be read as a flow.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DomainException($"The content cannot be read as a flow: {exception.Message}");
        }
    }

    public override string ToString() => $"{Name}@{Hash}";
}

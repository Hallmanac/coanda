namespace Coanda.Sdk.Contract;

/// <summary>
/// What a module answers to describe (<c>describe.schema.json</c>): the module API version and the
/// version it runs, the system steps it brings, the external event kinds it can report, the core
/// event kinds it subscribes to, and the settings it declares for each project: its replicated
/// settings and its credential profile setting.
/// </summary>
public sealed record DescribeAnswer(
    int ModuleApiVersion,
    string Version,
    IReadOnlyList<StepDescription> Steps,
    IReadOnlyList<EventKindDescription> EventKinds,
    IReadOnlyList<CoreEventKind> Subscriptions,
    SettingsSchema SettingsSchema);

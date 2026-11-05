namespace Coanda.Sdk.Contract;

/// <summary>
/// The settings a module declares for each project it is switched on for: its replicated settings,
/// each by name with a schema for its value, and the credential profile setting it may declare, whose
/// value is one of the node's profiles for the module, selected per project on each node.
/// </summary>
public sealed record SettingsSchema(IReadOnlyList<ReplicatedSetting> Replicated, CredentialProfileSetting? CredentialProfile = null);

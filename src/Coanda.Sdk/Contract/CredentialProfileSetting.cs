namespace Coanda.Sdk.Contract;

/// <summary>
/// The credential profile setting a module may declare: whether the module requires a profile, and
/// whether, when a project selects none on a node, the module acts as the node's default identity (the
/// GitHub CLI's default account, say), which then counts as the setting's value.
/// </summary>
public sealed record CredentialProfileSetting(bool Required, bool DefaultsToNodeIdentity);

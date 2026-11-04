namespace Coanda.Core.Projects.Events;

/// <summary>
/// The checks a merge of the project's pull requests waits for were named, replacing any named
/// before. Each name is recorded exactly as given and compared ordinally with GitHub's check run
/// names and commit status contexts.
/// </summary>
public sealed record ProjectRequiredChecksSet(
    Guid ProjectId,
    string Project,
    IReadOnlyList<string> Checks,
    string SetBy,
    DateTimeOffset SetAt);

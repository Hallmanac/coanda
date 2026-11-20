namespace Coanda.Daemon.Executors;

/// <summary>
/// One skill an agent step's session is given: the skill's <see cref="Name"/>, the <see cref="Module"/>
/// that ships it, by the name its manifest gives, and <see cref="Source"/>, the skill's folder in that
/// module's skills folder, as the daemon read it at start. An executor copies the whole folder into
/// the session's work directory before each process of the session starts.
/// </summary>
public sealed record SessionSkill(string Name, string Module, string Source);

using System.Text;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Daemon.Executors;

/// <summary>
/// The files a judgment's session reads, written under <c>.coanda/</c> at the root of the flow's work
/// directory: <c>task.md</c>, the parcel record, and <c>prompt.md</c>, the context the spawn carries,
/// which the runner writes before every spawn, and <c>persona.md</c>, which an adapter that hands its
/// vendor the persona as a file writes at its spawn and again before every resume, since the resumed
/// process reads it too. Each is written fresh, owner-only, and never read back by the daemon, so a
/// session that rewrites one changes nothing the next spawn sees, nor, for the persona, its resume.
/// Nothing else is written into the work directory.
/// </summary>
public static class SessionFiles
{
    public const string DirectoryName = ".coanda";

    /// <summary>The parcel record, relative to the work directory; every platform reads the forward slash.</summary>
    public const string TaskFile = DirectoryName + "/task.md";

    /// <summary>The context the spawn carries, relative to the work directory.</summary>
    public const string PromptFile = DirectoryName + "/prompt.md";

    /// <summary>The agent's persona, relative to the work directory.</summary>
    public const string PersonaFile = DirectoryName + "/persona.md";

    /// <summary>Writes <c>task.md</c> and <c>prompt.md</c>, creating the work directory when it is absent.</summary>
    public static void WriteInputs(string workingDirectory, Parcel parcel, ParcelType type, string context)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(type);
        Write(workingDirectory, TaskFile, RenderTask(parcel, type));
        Write(workingDirectory, PromptFile, context);
    }

    /// <summary>
    /// Writes one file under <c>.coanda/</c>, replacing whatever the session left at its path. A file
    /// or link left there is removed rather than written through, so a session that turned the path
    /// into a link to somewhere else never has the daemon write there.
    /// </summary>
    public static void Write(string workingDirectory, string relativePath, string content)
    {
        string directory = Path.Combine(workingDirectory, DirectoryName);
        Directory.CreateDirectory(workingDirectory);
        if (File.Exists(directory) || Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null)
        {
            File.Delete(directory);
        }

        OwnerOnly.EnsureDirectory(directory);
        string path = Path.Combine(workingDirectory, relativePath);
        File.Delete(path);
        using FileStream stream = OwnerOnly.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    /// <summary>The parcel's id, type, state, and title, and the value of every field its type declares, or "not set".</summary>
    public static string RenderTask(Parcel parcel, ParcelType type)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(type);
        var text = new StringBuilder();
        text.Append("# ").Append(parcel.Title).Append("\n\n");
        text.Append("- Parcel: ").Append(parcel.Id).Append('\n');
        text.Append("- Type: ").Append(parcel.TypeName).Append('\n');
        text.Append("- State: ").Append(parcel.State).Append('\n');
        text.Append("- Title: ").Append(parcel.Title).Append('\n');
        foreach (FieldDeclaration field in type.Fields)
        {
            text.Append("\n## ").Append(field.Name).Append("\n\n").Append(Value(parcel.ValueOf(field.Name))).Append('\n');
        }

        return text.ToString();
    }

    private static string Value(FieldValue? value) => value switch
    {
        { Text: { } held } => held,
        { Items: { } items } => string.Join('\n', items.Select(item => $"- {item}")),
        { Reference: { Url: { } url } reference } => $"{reference.Id} ({url})",
        { Reference: { } reference } => reference.Id,
        _ => ContextAssembler.NotSet,
    };
}

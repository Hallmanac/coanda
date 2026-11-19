using System.Text;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Daemon.Executors;

/// <summary>
/// The files an agent step's session reads, written under <c>.coanda/</c> at the root of the flow run's work
/// directory: <c>parcel.md</c>, the parcel record, and <c>prompt.md</c>, the context the spawn carries,
/// which the runner writes before every spawn, and <c>persona.md</c>, which an adapter that hands its
/// vendor the persona as a file writes at its spawn and again before every resume, since the resumed
/// process reads it too. Each is written fresh, owner-only, and never read back by the daemon, so a
/// session that rewrites one changes nothing the next spawn sees, nor, for the persona, its resume.
/// The daemon writes nothing else into the work directory; a module may write its own files there,
/// as the software module's review diff step writes <c>.coanda/review.diff</c> for the reviewers.
/// </summary>
public static class SessionFiles
{
    public const string DirectoryName = ".coanda";

    /// <summary>The parcel record, relative to the work directory; every platform reads the forward slash.</summary>
    public const string ParcelFile = DirectoryName + "/parcel.md";

    /// <summary>The context the spawn carries, relative to the work directory.</summary>
    public const string PromptFile = DirectoryName + "/prompt.md";

    /// <summary>The agent's persona, relative to the work directory.</summary>
    public const string PersonaFile = DirectoryName + "/persona.md";

    /// <summary>Writes <c>parcel.md</c> and <c>prompt.md</c>, creating the work directory when it is absent.</summary>
    public static void WriteInputs(string workingDirectory, Parcel parcel, ParcelType type, string context)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(type);
        Write(workingDirectory, ParcelFile, RenderParcel(parcel, type));
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

    /// <summary>
    /// The parcel's id, type, stage, and title, and the value of every field its type declares, or "not set",
    /// after <see cref="Fences.Framing"/>, which opens the file. The title is folded onto one line and set in an inline fence on
    /// both lines that carry it; every field value sits in fenced blocks that keep its line breaks, one
    /// block for a text, one per item of a list, and one each for a reference's id and URL. The words
    /// "not set" stay outside every fence, so an unset field and a text reading "not set" differ.
    /// </summary>
    public static string RenderParcel(Parcel parcel, ParcelType type)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(type);
        string title = Fences.Inline(parcel.Title);
        var text = new StringBuilder();
        text.Append(Fences.Framing).Append("\n\n");
        text.Append("# ").Append(title).Append("\n\n");
        text.Append("- Parcel: ").Append(parcel.Id).Append('\n');
        text.Append("- Type: ").Append(parcel.TypeName).Append('\n');
        text.Append("- Stage: ").Append(parcel.Stage).Append('\n');
        text.Append("- Title: ").Append(title).Append('\n');
        foreach (FieldDeclaration field in type.Fields)
        {
            text.Append("\n## ").Append(field.Name).Append("\n\n").Append(Value(parcel.ValueOf(field.Name))).Append('\n');
        }

        return text.ToString();
    }

    private static string Value(FieldValue? value) => value switch
    {
        { Text: { } held } => Fences.Block(held),
        { Items: { } items } => string.Join("\n\n", items.Select(Fences.Block)),
        { Reference: { Url: { } url } reference } => $"{Fences.Block(reference.Id)}\n\n{Fences.Block(url)}",
        { Reference: { } reference } => Fences.Block(reference.Id),
        _ => ContextAssembler.NotSet,
    };
}

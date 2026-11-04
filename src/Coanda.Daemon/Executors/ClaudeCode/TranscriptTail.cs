using System.Runtime.InteropServices;
using System.Text;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// Reads a transcript a process is still writing, from where the process's output began, a whole
/// line at a time: each read hands on the lines completed since the last one and keeps a line not
/// yet ended for a later read, unless it is told the output is complete. A transcript that does not
/// exist yet reads as empty.
/// </summary>
internal sealed class TranscriptTail(string path, long offset)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<byte> pending = [];
    private long position = offset;

    /// <summary>
    /// Hands every line completed since the last read to <paramref name="stream"/>, and, when
    /// <paramref name="toEnd"/>, the unended line after them too.
    /// </summary>
    public void ReadInto(ClaudeStream stream, bool toEnd)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > position)
            {
                file.Seek(position, SeekOrigin.Begin);
                byte[] read = new byte[file.Length - position];
                int count = file.ReadAtLeast(read, read.Length, throwOnEndOfStream: false);
                position += count;
                pending.AddRange(read.AsSpan(0, count));
            }
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        int start = 0;
        for (int index = pending.IndexOf((byte)'\n'); index >= 0; index = pending.IndexOf((byte)'\n', start))
        {
            stream.Read(Utf8.GetString(CollectionsMarshal.AsSpan(pending)[start..index]));
            start = index + 1;
        }

        pending.RemoveRange(0, start);
        if (toEnd && pending.Count > 0)
        {
            stream.Read(Utf8.GetString(CollectionsMarshal.AsSpan(pending)));
            pending.Clear();
        }
    }
}

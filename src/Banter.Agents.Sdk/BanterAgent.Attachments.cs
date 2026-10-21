using Banter.Client.Core;
using Banter.Protocol;

namespace Banter.Agents.Sdk;

/// <summary>
/// A file somebody attached to a message, as an agent may use it: what it is, and - when this
/// agent may be given it - the image as a data URI or the text as text.
/// </summary>
/// <param name="FileId">Banter's id for it; what <c>read_file</c> and the download take.</param>
/// <param name="Name">The name it was uploaded as.</param>
/// <param name="MimeType">Its media type.</param>
/// <param name="Size">In bytes.</param>
/// <param name="Note">One line for a model, in square brackets: what is attached and whether it is
/// included, or why it is not.</param>
/// <param name="ImageDataUri">The image, as <c>data:image/png;base64,…</c>, when it is one this agent
/// may be shown and is under the size limit; null otherwise.</param>
/// <param name="Text">The contents, when it is a small text file this agent may be shown; null otherwise.</param>
public sealed record Attachment(
    string FileId, string Name, string MimeType, long Size, string Note, string? ImageDataUri, string? Text)
{
    /// <summary>Whether the file itself came with this, rather than only word of it.</summary>
    public bool Shared => ImageDataUri is not null || Text is not null;
}

/// <summary>
/// Files people attach, for every agent and not only the model-backed one.
///
/// <para>Banter keeps a message's file in its own room-scoped store, and the client can fetch it
/// by id. <see cref="LlmChatAgent"/> did, privately, and showed the model the image; an agent
/// written against <see cref="BanterAgent"/> directly was handed the message's words - the
/// filename, when that is all a person typed - and nothing else, so it said no file had come
/// through while the file sat on the server. Reported from a real room. Now the turn carries the
/// file's id, <see cref="FetchAttachmentAsync(string?, CancellationToken)"/> fetches it under the
/// same rules the model-backed agent applies, an agent on the old contract is at least told what
/// was attached, and two tools let a model ask for a room's files itself.</para>
/// </summary>
public abstract partial class BanterAgent
{
    /// <summary>
    /// Produce a reply to <paramref name="prompt"/>, which came with the file <paramref name="fileId"/>
    /// when that is not null. Override this rather than the three-argument form to be handed the
    /// file: <see cref="FetchAttachmentAsync(string?, CancellationToken)"/> turns the id into the
    /// file, as far as this agent may be given it.
    ///
    /// <para>The default calls the three-argument form with the attachment described at the end of
    /// the prompt - its name, type, size and id, and how to read it - so an agent that knows nothing
    /// of files still tells the person what it was given and what it could not do with it.</para>
    /// </summary>
    protected virtual async IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt, string? fileId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (fileId is { Length: > 0 })
        {
            prompt = $"{prompt}\n\n{await AttachmentNoteAsync(fileId, cancellationToken).ConfigureAwait(false)}";
        }

        await foreach (var piece in RespondAsync(room, sender, prompt, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return piece;
        }
    }

    /// <summary>What to tell a model of a file it was not handed: enough to name it back and to
    /// ask for it with the tools.</summary>
    private async Task<string> AttachmentNoteAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            var info = await Client.GetFileInfoAsync(fileId, cancellationToken).ConfigureAwait(false);
            var image = info.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            return $"[Attached: {info.Name} ({info.MimeType}, {SizeText(info.Size)}); file id {fileId}. "
                 + (image
                     ? "It is an image, which only an agent built to show images to its model can show you."
                     : "Read it with read_file, giving that id.")
                 + "]";
        }
        catch (Exception ex) when (ex is BanterClientException or IOException)
        {
            return $"[A file was attached, but it could not be fetched: {ex.Message}]";
        }
    }

    /// <summary>Whether this agent is given attachments at all, by its own options.</summary>
    protected bool SharesAttachments => Options.ShareAttachments ?? Options.Locality == AgentLocality.Local;

    /// <summary>
    /// The file behind <paramref name="fileId"/>, as far as this agent may be given it: a local
    /// agent gets an image as an image and a small text file as its text; a third-party agent is
    /// told of the file and not sent it (see <see cref="BanterAgentOptions.ShareAttachments"/>).
    /// Null for a null or empty id; a file that cannot be fetched comes back as a note saying so.
    /// </summary>
    protected Task<Attachment?> FetchAttachmentAsync(string? fileId, CancellationToken cancellationToken = default) =>
        FetchAttachmentAsync(fileId, SharesAttachments, Options.MaxAttachmentImageBytes, Options.MaxAttachmentTextBytes, cancellationToken);

    /// <summary>As <see cref="FetchAttachmentAsync(string?, CancellationToken)"/>, with the rules
    /// given rather than read from the options.</summary>
    protected async Task<Attachment?> FetchAttachmentAsync(
        string? fileId, bool share, long maxImageBytes, long maxTextBytes, CancellationToken cancellationToken = default)
    {
        if (fileId is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var info = await Client.GetFileInfoAsync(fileId, cancellationToken).ConfigureAwait(false);
            var what = $"{info.Name} ({info.MimeType}, {SizeText(info.Size)})";
            var image = info.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var text = IsTextFile(info.MimeType, info.Name);

            if (!share)
            {
                return new Attachment(fileId, info.Name, info.MimeType, info.Size,
                    $"[A file is attached: {what}. It was not shared with you, because you "
                    + "are a third-party agent; say so if it matters to the answer.]", null, null);
            }

            if (image && info.Size <= maxImageBytes)
            {
                var bytes = await Client.DownloadFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                return new Attachment(fileId, info.Name, info.MimeType, info.Size,
                    $"[An image is attached: {what}. It is included with this message.]",
                    $"data:{info.MimeType};base64,{Convert.ToBase64String(bytes)}", null);
            }

            if (text && info.Size <= maxTextBytes)
            {
                var bytes = await Client.DownloadFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                var contents = System.Text.Encoding.UTF8.GetString(bytes);
                return new Attachment(fileId, info.Name, info.MimeType, info.Size,
                    $"[A file is attached: {what}. Its contents:]\n```\n{contents}\n```", null, contents);
            }

            return new Attachment(fileId, info.Name, info.MimeType, info.Size,
                $"[A file is attached: {what}. You cannot open files of this kind or "
                + "size; say so rather than guessing at what is in it.]", null, null);
        }
        catch (Exception ex) when (ex is BanterClientException or IOException)
        {
            return new Attachment(fileId, "", "", 0,
                $"[A file was attached, but it could not be fetched: {ex.Message}]", null, null);
        }
    }

    /// <summary>Whether a file is one whose contents can be read as text.</summary>
    protected static bool IsTextFile(string mime, string name) =>
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mime is "application/json" or "application/xml" or "application/x-yaml" or "application/yaml"
        || Path.GetExtension(name).ToLowerInvariant() is ".md" or ".txt" or ".csv" or ".json" or ".log"
            or ".yaml" or ".yml" or ".xml" or ".cs" or ".py" or ".js" or ".ts" or ".sql";

    /// <summary>A size as people read it.</summary>
    protected static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };

    /// <summary>Reading a room's files, for any agent's model to ask for itself.</summary>
    private IReadOnlyList<LocalTool> FileTools =>
    [
        new("room_files",
            "List the files people have attached in this room: name, type, size and the file id "
            + "that read_file takes. Use it when somebody refers to a file you were not handed.",
            """{"type":"object","properties":{}}""",
            async (room, _, ct) =>
            {
                var listing = await Client.ListFilesAsync(room, ct).ConfigureAwait(false);
                if (listing.Files.Count == 0)
                {
                    return $"No files have been attached in {room}.";
                }

                var lines = listing.Files.Select(f => $"- {f.Name} ({f.MimeType}, {SizeText(f.Size)}) - file id {f.FileId}");
                return $"Files attached in {room}:\n{string.Join("\n", lines)}";
            }),

        new("read_file",
            "Read a file attached in this room, by the file id from a message or from room_files. "
            + "A text file comes back as its text. An image cannot come back through a tool; you "
            + "are told what it is, and an agent built to show images to its model shows it with "
            + "the message instead.",
            """
            {"type":"object","properties":{
              "file_id":{"type":"string","description":"The file's id."}
            },"required":["file_id"]}
            """,
            async (_, args, ct) =>
            {
                var fileId = Text(args, "file_id");
                var attachment = await FetchAttachmentAsync(fileId, ct).ConfigureAwait(false);
                return attachment is null ? "No file id was given." : attachment.Note;
            }),
    ];
}

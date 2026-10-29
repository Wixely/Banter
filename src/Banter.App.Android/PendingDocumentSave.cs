namespace Banter.App.Android;

/// <summary>A process-owned save, independent of the activity that opened the picker.</summary>
internal sealed class PendingDocumentSave(byte[] content)
{
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<string?> Result => _done.Task;

    internal void Cancel() => _done.TrySetResult(null);

    internal void Fail(Exception error) => _done.TrySetException(error);

    internal async Task WriteAsync(Func<byte[], string?> write)
    {
        if (_done.Task.IsCompleted)
        {
            return;
        }

        try
        {
            // Content providers can block on disk or network I/O. Never run them in the
            // activity-result callback on Android's main thread.
            var where = await Task.Run(() => write(content)).ConfigureAwait(false);
            _done.TrySetResult(where);
        }
        catch (Exception ex)
        {
            // Deliver failures to the awaiting session, including Java provider exceptions.
            Fail(ex);
        }
    }
}

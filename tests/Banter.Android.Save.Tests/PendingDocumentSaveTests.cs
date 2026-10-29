using Banter.App.Android;
using Xunit;

namespace Banter.Android.Save.Tests;

public sealed class PendingDocumentSaveTests
{
    [Fact]
    public async Task SlowProviderDoesNotBlockTheResultCallback()
    {
        var save = new PendingDocumentSave([1, 2, 3]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new Thread(() => returned.SetResult(save.WriteAsync(bytes =>
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return "saved.json";
        })));
        callback.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var writing = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writing.IsCompleted);
            Assert.False(save.Result.IsCompleted);
        }
        finally
        {
            release.Set();
            callback.Join(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("saved.json", await save.Result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ANewResultHandlerCanFinishTheOriginalOperation()
    {
        // A recreated activity receives the process-owned operation, not a new task or bytes.
        var pending = new PendingDocumentSave([42]);
        var originalCaller = pending.Result;
        await HandleResult(pending);
        Assert.Equal("42", await originalCaller);

        static Task HandleResult(PendingDocumentSave operation) =>
            operation.WriteAsync(bytes => bytes[0].ToString());
    }

    [Fact]
    public async Task CancellationBeforeThePickerReturnsDoesNotWrite()
    {
        var pending = new PendingDocumentSave([42]);
        pending.Cancel();
        await pending.WriteAsync(_ => throw new InvalidOperationException("Must not write"));
        Assert.Null(await pending.Result);
    }

    [Fact]
    public async Task ProviderFailureReachesTheCallerInsteadOfEscapingTheCallback()
    {
        var pending = new PendingDocumentSave([42]);
        var failure = new IOException("Provider unavailable");
        await pending.WriteAsync(_ => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending.Result));
    }
}

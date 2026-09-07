using Banter.App;
using Banter.App.Desktop;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The remembered password. Tested through the real files rather than a fake, because every
/// interesting case here — a profile copied to another machine, a half-written file, a protector
/// that has changed — is a file that exists and cannot be read, and an in-memory double would
/// answer none of them.
/// </summary>
public sealed class StoredCredentialsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "banter-credentials-" + Guid.NewGuid().ToString("N"));

    private string At(string name = "credentials.dat") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var credential = new StoredCredentials("tcp://localhost:7770", "alice", "hunter2");

        Assert.True(credential.TrySave(At()));

        var loaded = StoredCredentials.Load(At());
        Assert.Equal(credential, loaded);
    }

    [Fact]
    public void NothingStoredIsNotAFailure()
    {
        // The first launch. Absent has to be ordinary, not an error somebody has to be told about.
        var problems = new List<string>();

        Assert.Null(StoredCredentials.Load(At(), problem: problems.Add));
        Assert.Empty(problems);
    }

    [Fact]
    public void AnUnreadableFileReadsAsNothingStored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(At(), "not json, and not protected either");

        var problems = new List<string>();

        // Null rather than a throw: to the person in front of it this means "sign in again",
        // which is the same thing an absent file means.
        Assert.Null(StoredCredentials.Load(At(), problem: problems.Add));

        // Still said, for anyone looking.
        Assert.Single(problems);
    }

    [Fact]
    public void AHalfWrittenCredentialIsRejectedRatherThanUsed()
    {
        // A record with an empty password would otherwise sail through and be handed to the
        // server as a sign-in attempt, which fails as "wrong password" and sends somebody looking
        // in entirely the wrong place.
        Assert.True(new StoredCredentials("tcp://localhost:7770", "alice", "").TrySave(At()));

        Assert.Null(StoredCredentials.Load(At()));
    }

    [Fact]
    public void SigningOutLeavesNothingBehind()
    {
        Assert.True(new StoredCredentials("tcp://localhost:7770", "alice", "hunter2").TrySave(At()));
        Assert.True(File.Exists(At()));

        Assert.True(StoredCredentials.TryDelete(At()));

        Assert.False(File.Exists(At()));
        Assert.Null(StoredCredentials.Load(At()));
    }

    [Fact]
    public void DeletingWhatIsNotThereSucceeds()
    {
        // Signing out twice, or signing out of a session that was never remembered.
        Assert.True(StoredCredentials.TryDelete(At()));
    }

    [Fact]
    public void EachSettingsProfileKeepsItsOwnCredential()
    {
        // The debug configurations point --settings at scratch profiles and would otherwise all
        // share one credential file in the real profile, so alice would sign in as whoever ran
        // last. This is the property that prevents it.
        var alice = StoredCredentials.PathFor(Path.Combine(_dir, "alice.json"));
        var bob = StoredCredentials.PathFor(Path.Combine(_dir, "bob.json"));

        Assert.NotEqual(alice, bob);
        Assert.Equal(_dir, Path.GetDirectoryName(alice));

        Assert.True(new StoredCredentials("tcp://localhost:7770", "alice", "a").TrySave(alice));
        Assert.True(new StoredCredentials("tcp://localhost:7770", "bob", "b").TrySave(bob));

        Assert.Equal("alice", StoredCredentials.Load(alice)!.User);
        Assert.Equal("bob", StoredCredentials.Load(bob)!.User);
    }

    [Fact]
    public void NoSettingsPathMeansTheDefaultProfile()
    {
        Assert.Equal(StoredCredentials.DefaultPath, StoredCredentials.PathFor(null));
        Assert.Equal(StoredCredentials.DefaultPath, StoredCredentials.PathFor("   "));
    }

    [Fact]
    public void TheProtectorIsActuallyApplied()
    {
        var protector = DpapiSecretProtector.ForThisMachine();

        Assert.True(new StoredCredentials("tcp://localhost:7770", "alice", "hunter2")
            .TrySave(At(), protector));

        var loaded = StoredCredentials.Load(At(), protector);
        Assert.Equal("hunter2", loaded!.Password);

        // On Windows the bytes on disk are DPAPI ciphertext, so the password must not be findable
        // in them. Elsewhere there is no OS store wired yet and this asserts nothing false: the
        // file is user-only and the sign-in screen says so.
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("hunter2", File.ReadAllText(At()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACredentialFromAnotherProtectorReadsAsNothingStored()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;                     // no protector to disagree with; nothing to assert
        }

        // A profile copied from another machine or another user account. It has to mean "sign in
        // again" rather than an exception out of a startup path with no window to report it in.
        Assert.True(new StoredCredentials("tcp://localhost:7770", "alice", "hunter2").TrySave(At()));

        Assert.Null(StoredCredentials.Load(At(), DpapiSecretProtector.ForThisMachine()));
    }
}

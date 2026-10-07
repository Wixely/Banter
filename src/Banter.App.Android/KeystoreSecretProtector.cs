using Android.Security.Keystore;
using Banter.App;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Banter.App.Android;

/// <summary>
/// The phone's answer to <see cref="ISecretProtector"/>: AES-GCM under a key the OS holds and
/// this process never sees.
///
/// <para>The app's own files directory is already private to the app, which is a real defence and
/// the reason this is not the only thing standing between a password and a reader. What the
/// Keystore adds is that the bytes are useless off the device: a backup, an adb pull from a
/// rooted phone, or a copy of the file alone decrypts to nothing, because the key is not in the
/// file and cannot be exported from the hardware that keeps it.</para>
///
/// <para>No user authentication is required on the key. It is deliberate: the credential exists so
/// that opening the app does not mean typing a password, and a key that demanded a fingerprint
/// first would have swapped one prompt for another.</para>
/// </summary>
public sealed class KeystoreSecretProtector : ISecretProtector
{
    private const string Provider = "AndroidKeyStore";
    private const string Alias = "banter.credentials.v1";
    private const string Transformation = "AES/GCM/NoPadding";

    /// <summary>GCM's standard nonce length. Written in front of the ciphertext because the
    /// cipher chooses it per encryption and decryption needs it back.</summary>
    private const int NonceBytes = 12;

    private const int TagBits = 128;

    public byte[] Protect(byte[] plaintext)
    {
        var cipher = Cipher.GetInstance(Transformation)
            ?? throw new PlatformNotSupportedException($"no {Transformation} on this device");
        cipher.Init(CipherMode.EncryptMode, Key());

        var nonce = cipher.GetIV() ?? throw new CryptographicProblem("the cipher produced no IV");
        var sealed_ = cipher.DoFinal(plaintext) ?? throw new CryptographicProblem("nothing came back");

        var output = new byte[nonce.Length + sealed_.Length];
        nonce.CopyTo(output, 0);
        sealed_.CopyTo(output, nonce.Length);
        return output;
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        if (protectedBytes.Length <= NonceBytes)
        {
            // Short of even a nonce, so there is nothing to try. Thrown rather than returned empty
            // because StoredCredentials.Load treats a throw as "sign in again", which is right.
            throw new CryptographicProblem($"{protectedBytes.Length} bytes is too short to be a credential");
        }

        var cipher = Cipher.GetInstance(Transformation)
            ?? throw new PlatformNotSupportedException($"no {Transformation} on this device");
        cipher.Init(
            CipherMode.DecryptMode,
            Key(),
            new GCMParameterSpec(TagBits, protectedBytes[..NonceBytes]));

        return cipher.DoFinal(protectedBytes[NonceBytes..])
            ?? throw new CryptographicProblem("nothing came back");
    }

    /// <summary>
    /// The key for this app, made once and kept by the OS from then on.
    ///
    /// <para>Created on demand rather than at startup: a phone that has never signed in should not
    /// be made to generate a key it may never use, and the first save is the moment it is needed.
    /// The alias carries a version so that changing the scheme means a key nothing else is using
    /// rather than a key two schemes disagree about - a credential under the old one then fails to
    /// read, which is already handled as "sign in again".</para>
    /// </summary>
    private static IKey Key()
    {
        var store = KeyStore.GetInstance(Provider)
            ?? throw new PlatformNotSupportedException($"no {Provider} on this device");
        store.Load(null);

        if (store.GetKey(Alias, null) is { } existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)
            ?? throw new PlatformNotSupportedException("no AES key generator on this device");

        generator.Init(new KeyGenParameterSpec.Builder(
                Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .Build());

        return generator.GenerateKey()
            ?? throw new CryptographicProblem("the Keystore generated no key");
    }

    /// <summary>
    /// What the settings page says about where a password is kept. Said in terms of what it means
    /// for the person - the phone, not this device - because that is the question being answered.
    /// </summary>
    public static string StorageDescription =>
        "Kept in this app's private storage, encrypted with a key held by Android's keystore - so a "
        + "copy of the file is useless on another device.";

    /// <summary>A failure from the platform's crypto that is not an exception type it throws.</summary>
    private sealed class CryptographicProblem(string message) : Exception(message);
}

namespace OnlyShell.Core.Sync;

public interface ISyncPayloadProtector
{
    string Protect(string plaintext, string passphrase);
    string Unprotect(string encryptedEnvelope, string passphrase);
}

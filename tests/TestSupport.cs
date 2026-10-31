using System.Text;

namespace Maela.Tests
{
    /// <summary>A fixed host identity so tests can build the fingerprint a room code would carry.</summary>
    internal static class VoipEngineTestKeys
    {
        public static readonly byte[] HostPrivateKey = Hkdf.DeriveKey(null, Encoding.ASCII.GetBytes("maela test host"), null, 32);
        public static readonly byte[] HostFingerprint = VoipEngine.Fingerprint(X25519.PublicKey(HostPrivateKey));
    }
}

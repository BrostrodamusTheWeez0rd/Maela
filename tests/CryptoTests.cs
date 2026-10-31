using System;
using System.Security.Cryptography;

namespace Maela.Tests
{
    // Known-answer tests for the crypto primitives (RFC 7748, RFC 5869, FIPS-197) and
    // behaviour tests for packet sealing (tamper, replay, reordering).
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class CryptoTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        static byte[] Hex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        static string ToHex(byte[] b)
        {
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        static void X25519Vectors()
        {
            // RFC 7748 section 5.2, first test vector.
            byte[] r = X25519.ScalarMult(
                Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"));
            Check(ToHex(r) == "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552", "X25519 RFC 7748 5.2 vector 1");

            // RFC 7748 section 5.2, iterated test (k = u = 9).
            byte[] k = Hex("0900000000000000000000000000000000000000000000000000000000000000");
            byte[] u = Hex("0900000000000000000000000000000000000000000000000000000000000000");
            for (int i = 1; i <= 1000; i++)
            {
                byte[] next = X25519.ScalarMult(k, u);
                u = k;
                k = next;
                if (i == 1)
                    Check(ToHex(k) == "422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079", "X25519 RFC 7748 iterated x1");
            }
            Check(ToHex(k) == "684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51", "X25519 RFC 7748 iterated x1000");

            // RFC 7748 section 6.1 Diffie-Hellman.
            byte[] alicePriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            byte[] bobPriv = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
            byte[] alicePub = X25519.PublicKey(alicePriv);
            byte[] bobPub = X25519.PublicKey(bobPriv);
            Check(ToHex(alicePub) == "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", "X25519 RFC 7748 6.1 Alice public key");
            Check(ToHex(bobPub) == "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", "X25519 RFC 7748 6.1 Bob public key");
            string shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
            Check(ToHex(X25519.SharedSecret(alicePriv, bobPub)) == shared && ToHex(X25519.SharedSecret(bobPriv, alicePub)) == shared, "X25519 RFC 7748 6.1 shared secret");

            bool rejected = false;
            try { X25519.SharedSecret(alicePriv, new byte[32]); }
            catch (CryptographicException) { rejected = true; }
            Check(rejected, "X25519 rejects the all-zero (low-order) peer key");

            byte[] p1 = X25519.GeneratePrivateKey(), p2 = X25519.GeneratePrivateKey();
            Check(ToHex(X25519.SharedSecret(p1, X25519.PublicKey(p2))) == ToHex(X25519.SharedSecret(p2, X25519.PublicKey(p1))), "X25519 random keys agree");
        }

        static void HkdfVectors()
        {
            // RFC 5869 test case 1.
            byte[] ikm = new byte[22];
            for (int i = 0; i < ikm.Length; i++) ikm[i] = 0x0b;
            byte[] okm = Hkdf.DeriveKey(Hex("000102030405060708090a0b0c"), ikm, Hex("f0f1f2f3f4f5f6f7f8f9"), 42);
            Check(ToHex(okm) == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF-SHA256 RFC 5869 test case 1");
        }

        static void AesVector()
        {
            // FIPS-197 appendix C.3 (AES-256), the block cipher under our CTR mode.
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
                byte[] ct = new byte[16];
                aes.CreateEncryptor().TransformBlock(Hex("00112233445566778899aabbccddeeff"), 0, 16, ct, 0);
                Check(ToHex(ct) == "8ea2b7ca516745bfeafc49904b496089", "AES-256 FIPS-197 C.3");
            }
        }

        static void PacketSealing()
        {
            byte[] key = CryptoUtil.Random(64);
            PacketCipher sender = new PacketCipher(key);
            PacketCipher receiver = new PacketCipher(key);
            byte[] ad = new byte[] { 1, 2, 3 };
            byte[] message = new byte[100];
            for (int i = 0; i < message.Length; i++) message[i] = (byte)i;

            byte[] sealed1 = sender.Seal(ad, message);
            Check(sealed1.Length == ad.Length + message.Length + PacketCipher.Overhead, "sealed size = message + 24 bytes");
            byte[] opened = receiver.Open(sealed1, sealed1.Length, ad.Length);
            Check(opened != null && ToHex(opened) == ToHex(message), "round trip");
            Check(receiver.Open(sealed1, sealed1.Length, ad.Length) == null, "replayed packet rejected");

            bool ciphertextHidden = true;
            for (int i = 0; i + 8 < message.Length; i++)
                if (sealed1[ad.Length + PacketCipher.CounterSize + i] == message[i] && sealed1[ad.Length + PacketCipher.CounterSize + i + 1] == message[i + 1] && sealed1[ad.Length + PacketCipher.CounterSize + i + 2] == message[i + 2])
                    ciphertextHidden = false;
            Check(ciphertextHidden, "plaintext not visible in packet");

            byte[] sealed2 = sender.Seal(ad, message);
            byte[] tampered = (byte[])sealed2.Clone();
            tampered[ad.Length + 12] ^= 1;
            Check(receiver.Open(tampered, tampered.Length, ad.Length) == null, "flipped ciphertext bit rejected");
            tampered = (byte[])sealed2.Clone();
            tampered[0] ^= 1;
            Check(receiver.Open(tampered, tampered.Length, ad.Length) == null, "modified associated data rejected");
            Check(new PacketCipher(CryptoUtil.Random(64)).Open(sealed2, sealed2.Length, ad.Length) == null, "wrong key rejected");

            // Out-of-order delivery within the window is fine; very old packets are not.
            byte[][] burst = new byte[2000][];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = sender.Seal(ad, message);
            Check(receiver.Open(burst[10], burst[10].Length, ad.Length) != null, "packet 10 accepted");
            Check(receiver.Open(burst[5], burst[5].Length, ad.Length) != null, "earlier packet 5 accepted (reordered)");
            Check(receiver.Open(burst[5], burst[5].Length, ad.Length) == null, "packet 5 replay rejected");
            Check(receiver.Open(burst[1999], burst[1999].Length, ad.Length) != null, "packet 1999 accepted");
            Check(receiver.Open(burst[20], burst[20].Length, ad.Length) == null, "packet older than the replay window rejected");
            Check(receiver.Open(burst[1500], burst[1500].Length, ad.Length) != null, "packet inside the window accepted");
        }

        static int Main()
        {
            X25519Vectors();
            HkdfVectors();
            AesVector();
            PacketSealing();
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All crypto checks passed." : _failures + " crypto check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}

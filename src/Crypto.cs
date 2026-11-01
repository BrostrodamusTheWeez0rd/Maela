using System;
using System.Security.Cryptography;

namespace Maela
{
    /// <summary>
    /// X25519 Diffie-Hellman (RFC 7748). Port of the TweetNaCl reference implementation:
    /// small, constant-time (no secret-dependent branches or table lookups) and verified
    /// against the RFC test vectors in tests\CryptoTests.cs.
    /// </summary>
    internal static class X25519
    {
        public const int KeySize = 32;

        private static readonly long[] A24 = { 0xDB41, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] BasePoint = { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>Creates a new random private key.</summary>
        public static byte[] GeneratePrivateKey()
        {
            byte[] key = new byte[KeySize];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(key);
            key[0] &= 248;
            key[31] &= 127;
            key[31] |= 64;
            return key;
        }

        public static byte[] PublicKey(byte[] privateKey)
        {
            return ScalarMult(privateKey, BasePoint);
        }

        /// <summary>Computes the shared secret. Throws if the peer's key is a low-order point.</summary>
        public static byte[] SharedSecret(byte[] privateKey, byte[] peerPublicKey)
        {
            byte[] shared = ScalarMult(privateKey, peerPublicKey);
            int acc = 0;
            int i;
            for (i = 0; i < KeySize; i++)
                acc |= shared[i];
            if (acc == 0)
                throw new CryptographicException("Invalid peer key.");
            return shared;
        }

        public static byte[] ScalarMult(byte[] scalar, byte[] point)
        {
            if (scalar == null || scalar.Length != KeySize || point == null || point.Length != KeySize)
                throw new ArgumentException("X25519 keys are 32 bytes.");

            byte[] z = new byte[32];
            Buffer.BlockCopy(scalar, 0, z, 0, 32);
            z[31] = (byte)((scalar[31] & 127) | 64);
            z[0] &= 248;

            long[] x = new long[16];
            Unpack(x, point);
            long[] a = new long[16], b = new long[16], c = new long[16], d = new long[16], e = new long[16], f = new long[16];
            int i;
            for (i = 0; i < 16; i++)
                b[i] = x[i];
            a[0] = 1;
            d[0] = 1;

            for (i = 254; i >= 0; --i)
            {
                long r = (z[i >> 3] >> (i & 7)) & 1;
                Select(a, b, r);
                Select(c, d, r);
                Add(e, a, c);
                Sub(a, a, c);
                Add(c, b, d);
                Sub(b, b, d);
                Mul(d, e, e);
                Mul(f, a, a);
                Mul(a, c, a);
                Mul(c, b, e);
                Add(e, a, c);
                Sub(a, a, c);
                Mul(b, a, a);
                Sub(c, d, f);
                Mul(a, c, A24);
                Add(a, a, d);
                Mul(c, c, a);
                Mul(a, d, f);
                Mul(d, b, x);
                Mul(b, e, e);
                Select(a, b, r);
                Select(c, d, r);
            }

            Invert(c, c);
            Mul(a, a, c);
            byte[] output = new byte[32];
            Pack(output, a);
            Array.Clear(z, 0, z.Length);
            return output;
        }

        private static void Carry(long[] o)
        {
            int i;
            for (i = 0; i < 16; i++)
            {
                o[i] += 1L << 16;
                long c = o[i] >> 16;
                if (i < 15)
                    o[i + 1] += c - 1;
                else
                    o[0] += 38 * (c - 1);
                o[i] -= c << 16;
            }
        }

        private static void Select(long[] p, long[] q, long bit)
        {
            long c = ~(bit - 1);
            int i;
            for (i = 0; i < 16; i++)
            {
                long t = c & (p[i] ^ q[i]);
                p[i] ^= t;
                q[i] ^= t;
            }
        }

        private static void Pack(byte[] o, long[] n)
        {
            long[] m = new long[16];
            long[] t = new long[16];
            int i, j;
            for (i = 0; i < 16; i++)
                t[i] = n[i];
            Carry(t);
            Carry(t);
            Carry(t);
            for (j = 0; j < 2; j++)
            {
                m[0] = t[0] - 0xffed;
                for (i = 1; i < 15; i++)
                {
                    m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                    m[i - 1] &= 0xffff;
                }
                m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
                long b = (m[15] >> 16) & 1;
                m[14] &= 0xffff;
                Select(t, m, 1 - b);
            }
            for (i = 0; i < 16; i++)
            {
                o[2 * i] = (byte)(t[i] & 0xff);
                o[2 * i + 1] = (byte)(t[i] >> 8);
            }
        }

        private static void Unpack(long[] o, byte[] n)
        {
            int i;
            for (i = 0; i < 16; i++)
                o[i] = n[2 * i] + ((long)n[2 * i + 1] << 8);
            o[15] &= 0x7fff;
        }

        private static void Add(long[] o, long[] a, long[] b)
        {
            int i;
            for (i = 0; i < 16; i++)
                o[i] = a[i] + b[i];
        }

        private static void Sub(long[] o, long[] a, long[] b)
        {
            int i;
            for (i = 0; i < 16; i++)
                o[i] = a[i] - b[i];
        }

        private static void Mul(long[] o, long[] a, long[] b)
        {
            long[] t = new long[31];
            int i, j;
            for (i = 0; i < 16; i++)
                for (j = 0; j < 16; j++)
                    t[i + j] += a[i] * b[j];
            for (i = 0; i < 15; i++)
                t[i] += 38 * t[i + 16];
            for (i = 0; i < 16; i++)
                o[i] = t[i];
            Carry(o);
            Carry(o);
        }

        private static void Invert(long[] o, long[] input)
        {
            long[] c = new long[16];
            int a;
            for (a = 0; a < 16; a++)
                c[a] = input[a];
            for (a = 253; a >= 0; a--)
            {
                Mul(c, c, c);
                if (a != 2 && a != 4)
                    Mul(c, c, input);
            }
            for (a = 0; a < 16; a++)
                o[a] = c[a];
        }
    }

    /// <summary>HKDF with HMAC-SHA256 (RFC 5869).</summary>
    internal static class Hkdf
    {
        public static byte[] DeriveKey(byte[] salt, byte[] inputKeyMaterial, byte[] info, int length)
        {
            byte[] prk;
            using (HMACSHA256 extract = new HMACSHA256(salt == null || salt.Length == 0 ? new byte[32] : salt))
                prk = extract.ComputeHash(inputKeyMaterial);

            byte[] output = new byte[length];
            byte[] previous = new byte[0];
            int written = 0;
            byte counter = 1;
            using (HMACSHA256 expand = new HMACSHA256(prk))
            {
                while (written < length)
                {
                    byte[] block = new byte[previous.Length + (info == null ? 0 : info.Length) + 1];
                    Buffer.BlockCopy(previous, 0, block, 0, previous.Length);
                    if (info != null)
                        Buffer.BlockCopy(info, 0, block, previous.Length, info.Length);
                    block[block.Length - 1] = counter++;
                    previous = expand.ComputeHash(block);
                    int take = Math.Min(previous.Length, length - written);
                    Buffer.BlockCopy(previous, 0, output, written, take);
                    written += take;
                }
            }
            Array.Clear(prk, 0, prk.Length);
            return output;
        }
    }

    /// <summary>
    /// Encrypts and authenticates packets in one direction of a session:
    /// AES-256-CTR, then HMAC-SHA256 over (associated data || counter || ciphertext),
    /// truncated to 16 bytes. Each packet carries a 64-bit counter that is never reused
    /// and doubles as the CTR nonce. Receivers reject replays with a sliding window.
    /// </summary>
    internal sealed class PacketCipher : IDisposable
    {
        public const int TagSize = 16;
        public const int CounterSize = 8;
        public const int Overhead = CounterSize + TagSize;

        private const int ReplayWindow = 1024;

        private readonly object _lock = new object();
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly HMACSHA256 _mac;
        private readonly byte[] _counterBlock = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private readonly ulong[] _window = new ulong[ReplayWindow / 64];
        private ulong _sendCounter;
        private ulong _highestReceived;
        private bool _receivedAny;

        /// <param name="key">64 bytes: 32 for AES, 32 for HMAC.</param>
        public PacketCipher(byte[] key)
        {
            if (key == null || key.Length != 64)
                throw new ArgumentException("Packet keys are 64 bytes.");
            byte[] encKey = new byte[32];
            byte[] macKey = new byte[32];
            Buffer.BlockCopy(key, 0, encKey, 0, 32);
            Buffer.BlockCopy(key, 32, macKey, 0, 32);

            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = encKey;
            _ecb = _aes.CreateEncryptor();
            _mac = new HMACSHA256(macKey);
            Array.Clear(encKey, 0, 32);
            Array.Clear(macKey, 0, 32);
        }

        /// <summary>Returns associatedData || counter || ciphertext || tag.</summary>
        public byte[] Seal(byte[] associatedData, byte[] plaintext)
        {
            int adLength = associatedData == null ? 0 : associatedData.Length;
            byte[] output = new byte[adLength + CounterSize + plaintext.Length + TagSize];
            lock (_lock)
            {
                ulong counter = ++_sendCounter;
                if (adLength > 0)
                    Buffer.BlockCopy(associatedData, 0, output, 0, adLength);
                WriteCounter(output, adLength, counter);
                Crypt(counter, plaintext, 0, output, adLength + CounterSize, plaintext.Length);
                byte[] tag = _mac.ComputeHash(output, 0, adLength + CounterSize + plaintext.Length);
                Buffer.BlockCopy(tag, 0, output, output.Length - TagSize, TagSize);
            }
            return output;
        }

        /// <summary>
        /// Verifies and decrypts a sealed packet whose first <paramref name="adLength"/> bytes are
        /// associated data. Returns null if it is forged, corrupted or replayed.
        /// </summary>
        public byte[] Open(byte[] packet, int length, int adLength)
        {
            int cipherLength = length - adLength - CounterSize - TagSize;
            if (packet == null || cipherLength < 0)
                return null;

            lock (_lock)
            {
                byte[] tag = _mac.ComputeHash(packet, 0, length - TagSize);
                int diff = 0;
                int i;
                for (i = 0; i < TagSize; i++)
                    diff |= tag[i] ^ packet[length - TagSize + i];
                if (diff != 0)
                    return null;

                ulong counter = ReadCounter(packet, adLength);
                if (counter == 0 || !AcceptCounter(counter))
                    return null;

                byte[] plaintext = new byte[cipherLength];
                Crypt(counter, packet, adLength + CounterSize, plaintext, 0, cipherLength);
                return plaintext;
            }
        }

        private bool AcceptCounter(ulong counter)
        {
            if (!_receivedAny || counter > _highestReceived)
            {
                ulong shift = _receivedAny ? counter - _highestReceived : ReplayWindow;
                if (shift >= ReplayWindow)
                {
                    Array.Clear(_window, 0, _window.Length);
                }
                else
                {
                    ulong s;
                    for (s = 0; s < shift; s++)
                    {
                        ulong bit = (_highestReceived + 1 + s) % ReplayWindow;
                        _window[bit / 64] &= ~(1UL << (int)(bit % 64));
                    }
                }
                _highestReceived = counter;
                _receivedAny = true;
                ulong slot = counter % ReplayWindow;
                _window[slot / 64] |= 1UL << (int)(slot % 64);
                return true;
            }

            if (_highestReceived - counter >= ReplayWindow)
                return false; // Too old to tell.
            ulong index = counter % ReplayWindow;
            ulong mask = 1UL << (int)(index % 64);
            if ((_window[index / 64] & mask) != 0)
                return false; // Replay.
            _window[index / 64] |= mask;
            return true;
        }

        private void Crypt(ulong counter, byte[] input, int inputOffset, byte[] output, int outputOffset, int length)
        {
            // Counter block: 8-byte packet counter || 8-byte block index (big-endian).
            WriteCounter(_counterBlock, 0, counter);
            ulong block = 0;
            int done = 0;
            while (done < length)
            {
                int k;
                for (k = 0; k < 8; k++)
                    _counterBlock[8 + k] = (byte)(block >> (56 - 8 * k));
                _ecb.TransformBlock(_counterBlock, 0, 16, _keystream, 0);
                int take = Math.Min(16, length - done);
                for (k = 0; k < take; k++)
                    output[outputOffset + done + k] = (byte)(input[inputOffset + done + k] ^ _keystream[k]);
                done += take;
                block++;
            }
        }

        private static void WriteCounter(byte[] buffer, int offset, ulong counter)
        {
            int k;
            for (k = 0; k < 8; k++)
                buffer[offset + k] = (byte)(counter >> (56 - 8 * k));
        }

        private static ulong ReadCounter(byte[] buffer, int offset)
        {
            ulong value = 0;
            int k;
            for (k = 0; k < 8; k++)
                value = (value << 8) | buffer[offset + k];
            return value;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _ecb.Dispose();
                _aes.Dispose();
                _mac.Dispose();
            }
        }
    }

    internal static class CryptoUtil
    {
        public static byte[] Random(int length)
        {
            byte[] bytes = new byte[length];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] Sha256(params byte[][] parts)
        {
            using (SHA256 sha = SHA256.Create())
            {
                int i;
                for (i = 0; i < parts.Length; i++)
                    sha.TransformBlock(parts[i], 0, parts[i].Length, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int diff = 0;
            int i;
            for (i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}

namespace Maela
{
    /// <summary>Encrypts secrets at rest with Windows DPAPI, bound to the current Windows user.</summary>
    internal static class SecretStore
    {
        private static readonly byte[] Entropy = System.Text.Encoding.ASCII.GetBytes("maela secret v1");

        public static string Protect(byte[] secret)
        {
            byte[] protectedBytes = ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        /// <summary>Returns null if the value can't be decrypted (for example, copied from another account).</summary>
        public static byte[] Unprotect(string value)
        {
            try
            {
                return ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A whole file encrypted with DPAPI, so nothing in it is readable on disk. The protection
    /// comes from the Windows account's own keys, not from anything in this source code.
    /// </summary>
    internal static class EncryptedFile
    {
        private static readonly byte[] Entropy = System.Text.Encoding.ASCII.GetBytes("maela file v1");

        /// <summary>
        /// Returns null if the contents can't be decrypted (damaged, or from another account).
        /// Throws if the file can't be read at all, which says nothing about its contents.
        /// </summary>
        public static byte[] Read(string path)
        {
            byte[] stored = System.IO.File.ReadAllBytes(path);
            try
            {
                return ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>Moves a file that can't be used out of the way without destroying it.</summary>
        public static bool SetAside(string path)
        {
            try
            {
                string target = System.IO.Path.ChangeExtension(path, ".unreadable");
                int n = 1;
                while (System.IO.File.Exists(target))
                    target = System.IO.Path.ChangeExtension(path, ".unreadable" + n++);
                System.IO.File.Move(path, target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Replaces the file in one step, so a crash leaves the old or the new contents, never half.</summary>
        public static void Write(string path, byte[] data)
        {
            byte[] stored = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            string temp = path + ".tmp";
            try
            {
                using (System.IO.FileStream file = new System.IO.FileStream(temp, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                {
                    file.Write(stored, 0, stored.Length);
                    file.Flush(true);
                }
                if (System.IO.File.Exists(path))
                    System.IO.File.Replace(temp, path, null);
                else
                    System.IO.File.Move(temp, path);
            }
            finally
            {
                try { System.IO.File.Delete(temp); }
                catch { }
            }
        }
    }
}

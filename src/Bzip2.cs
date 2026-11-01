using System;
using System.IO;

namespace Maela
{
    /// <summary>
    /// A small bzip2 decompressor (Cisco ships OpenH264 as .dll.bz2, and .NET Framework has no
    /// bzip2). Only ever used on a download whose SHA-256 has already been checked against a
    /// hash built into mæla, and the output is capped.
    /// </summary>
    internal static class Bzip2
    {
        public static byte[] Decompress(byte[] input, int maxOutput)
        {
            BitReader bits = new BitReader(input);
            if (bits.Read(8) != 'B' || bits.Read(8) != 'Z' || bits.Read(8) != 'h')
                throw new InvalidDataException("Not a bzip2 file.");
            int level = bits.Read(8) - '0';
            if (level < 1 || level > 9)
                throw new InvalidDataException("Bad bzip2 block size.");
            int blockMax = level * 100000;

            MemoryStream output = new MemoryStream();
            uint combinedCrc = 0;
            int[] tt = new int[blockMax];
            while (true)
            {
                long magic = ((long)bits.Read(24) << 24) | (uint)bits.Read(24);
                uint storedCrc = (uint)bits.Read(32);
                if (magic == 0x177245385090L)
                {
                    if (storedCrc != combinedCrc)
                        throw new InvalidDataException("bzip2 stream checksum mismatch.");
                    break;
                }
                if (magic != 0x314159265359L)
                    throw new InvalidDataException("Bad bzip2 block.");
                uint blockCrc = DecodeBlock(bits, tt, blockMax, output, maxOutput);
                if (blockCrc != storedCrc)
                    throw new InvalidDataException("bzip2 block checksum mismatch.");
                combinedCrc = ((combinedCrc << 1) | (combinedCrc >> 31)) ^ blockCrc;
            }
            return output.ToArray();
        }

        private static uint DecodeBlock(BitReader bits, int[] tt, int blockMax, MemoryStream output, int maxOutput)
        {
            if (bits.Read(1) != 0)
                throw new InvalidDataException("Randomised bzip2 blocks aren't supported.");
            int origPtr = bits.Read(24);

            // Symbol map.
            bool[] used = new bool[256];
            int usedGroups = bits.Read(16);
            for (int i = 0; i < 16; i++)
            {
                if ((usedGroups & (0x8000 >> i)) == 0)
                    continue;
                int group = bits.Read(16);
                for (int j = 0; j < 16; j++)
                    if ((group & (0x8000 >> j)) != 0) used[i * 16 + j] = true;
            }
            byte[] seqToUnseq = new byte[256];
            int inUse = 0;
            for (int i = 0; i < 256; i++)
                if (used[i]) seqToUnseq[inUse++] = (byte)i;
            if (inUse == 0)
                throw new InvalidDataException("Empty bzip2 symbol map.");
            int alphaSize = inUse + 2;

            int groups = bits.Read(3);
            int selectors = bits.Read(15);
            if (groups < 2 || groups > 6 || selectors < 1)
                throw new InvalidDataException("Bad bzip2 tables.");
            byte[] mtfGroups = new byte[groups];
            for (int i = 0; i < groups; i++) mtfGroups[i] = (byte)i;
            byte[] selector = new byte[selectors];
            for (int i = 0; i < selectors; i++)
            {
                int j = 0;
                while (bits.Read(1) == 1)
                {
                    if (++j >= groups) throw new InvalidDataException("Bad bzip2 selector.");
                }
                byte v = mtfGroups[j];
                for (; j > 0; j--) mtfGroups[j] = mtfGroups[j - 1];
                mtfGroups[0] = v;
                selector[i] = v;
            }

            // Huffman tables (canonical codes from lengths).
            int[][] limit = new int[groups][], bases = new int[groups][], perm = new int[groups][];
            int[] minLen = new int[groups];
            for (int t = 0; t < groups; t++)
            {
                int[] length = new int[alphaSize];
                int curr = bits.Read(5);
                for (int i = 0; i < alphaSize; i++)
                {
                    while (true)
                    {
                        if (curr < 1 || curr > 20) throw new InvalidDataException("Bad bzip2 code length.");
                        if (bits.Read(1) == 0) break;
                        curr += bits.Read(1) == 0 ? 1 : -1;
                    }
                    length[i] = curr;
                }
                int min = 32, max = 0;
                for (int i = 0; i < alphaSize; i++) { min = Math.Min(min, length[i]); max = Math.Max(max, length[i]); }
                minLen[t] = min;
                limit[t] = new int[22]; bases[t] = new int[22]; perm[t] = new int[alphaSize];
                int pp = 0;
                for (int len = min; len <= max; len++)
                    for (int s = 0; s < alphaSize; s++)
                        if (length[s] == len) perm[t][pp++] = s;
                int[] count = new int[22];
                for (int i = 0; i < alphaSize; i++) count[length[i]]++;
                int code = 0, index = 0;
                for (int len = 0; len <= 20; len++)
                {
                    bases[t][len] = index - code;          // symbolIndex = code + bases[len]
                    code += count[len];
                    index += count[len];
                    limit[t][len] = code - 1;              // largest code of this length
                    code <<= 1;
                }
            }

            // Huffman + MTF + RUNA/RUNB decode into tt (byte values), counting.
            byte[] mtf = new byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            int[] unzftab = new int[256];
            int eob = inUse + 1;
            int n = 0, groupIndex = -1, groupLeft = 0, table = 0;
            int runLength = 0, runBit = 1;
            while (true)
            {
                if (groupLeft == 0)
                {
                    if (++groupIndex >= selectors) throw new InvalidDataException("bzip2 selectors exhausted.");
                    table = selector[groupIndex];
                    groupLeft = 50;
                }
                groupLeft--;
                int len = minLen[table];
                int v = bits.Read(len);
                while (len <= 20 && v > limit[table][len])
                {
                    len++;
                    v = (v << 1) | bits.Read(1);
                }
                if (len > 20) throw new InvalidDataException("Bad bzip2 code.");
                int symbolIndex = v + bases[table][len];
                if (symbolIndex < 0 || symbolIndex >= alphaSize) throw new InvalidDataException("Bad bzip2 code.");
                int sym = perm[table][symbolIndex];

                if (sym <= 1) // RUNA / RUNB
                {
                    runLength += (sym + 1) * runBit;
                    runBit <<= 1;
                    if (runLength > blockMax) throw new InvalidDataException("bzip2 run too long.");
                    continue;
                }
                if (runLength > 0)
                {
                    byte b = seqToUnseq[mtf[0]];
                    if (n + runLength > blockMax) throw new InvalidDataException("bzip2 block too long.");
                    unzftab[b] += runLength;
                    for (int i = 0; i < runLength; i++) tt[n++] = b;
                    runLength = 0; runBit = 1;
                }
                if (sym == eob)
                    break;
                int pos = sym - 1;
                byte value = mtf[pos];
                Buffer.BlockCopy(mtf, 0, mtf, 1, pos);
                mtf[0] = value;
                if (n >= blockMax) throw new InvalidDataException("bzip2 block too long.");
                byte outByte = seqToUnseq[value];
                unzftab[outByte]++;
                tt[n++] = outByte;
            }
            if (origPtr < 0 || origPtr >= n)
                throw new InvalidDataException("Bad bzip2 origin.");

            // Inverse BWT.
            int[] cftab = new int[257];
            for (int i = 0; i < 256; i++) cftab[i + 1] = cftab[i] + unzftab[i];
            for (int i = 0; i < n; i++)
            {
                int b = tt[i] & 0xFF;
                tt[cftab[b]++] |= i << 8;
            }

            // Undo the initial run-length encoding while writing, with CRC.
            uint crc = 0xFFFFFFFF;
            int p = tt[origPtr] >> 8;
            int last = -1, repeat = 0;
            for (int i = 0; i < n; i++)
            {
                int entry = tt[p];
                int b = entry & 0xFF;
                p = entry >> 8;
                if (repeat == 4) // Four equal bytes are followed by an extra-count byte.
                {
                    for (int k = 0; k < b; k++) crc = WriteByte(output, (byte)last, crc, maxOutput);
                    repeat = 0;
                    last = -1;
                    continue;
                }
                crc = WriteByte(output, (byte)b, crc, maxOutput);
                if (b == last) repeat++;
                else { repeat = 1; last = b; }
            }
            return ~crc;
        }

        private static uint WriteByte(MemoryStream output, byte b, uint crc, int maxOutput)
        {
            if (output.Length >= maxOutput)
                throw new InvalidDataException("bzip2 output too large.");
            output.WriteByte(b);
            return (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++)
                    c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                table[i] = c;
            }
            return table;
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _byte;
            private int _bit; // Bits already used from _data[_byte], from the top.

            public BitReader(byte[] data) { _data = data; }

            public int Read(int count)
            {
                int value = 0;
                for (int i = 0; i < count; i++)
                {
                    if (_byte >= _data.Length)
                        throw new InvalidDataException("bzip2 data ended early.");
                    value = (value << 1) | ((_data[_byte] >> (7 - _bit)) & 1);
                    if (++_bit == 8) { _bit = 0; _byte++; }
                }
                return value;
            }
        }
    }
}

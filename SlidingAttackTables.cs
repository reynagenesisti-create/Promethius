using System;
using System.Numerics;

namespace PromethiusEngine
{
    // Precomputed sliding attack tables per-square. This implementation builds
    // masks and full attack tables (indexed by occupancy subset of the mask).
    // It is functionally equivalent to magic-bitboard tables (precomputed
    // attacks) but uses direct occupancy-compression for indexing. This
    // keeps the implementation simple and reliable.
    public static class SlidingAttackTables
    {
        public static readonly ulong[] RookMask = new ulong[64];
        public static readonly ulong[] BishopMask = new ulong[64];

        private static readonly int[] RookRelevantBits = new int[64];
        private static readonly int[] BishopRelevantBits = new int[64];

        private static readonly ulong[][] RookAttacks = new ulong[64][];
        private static readonly ulong[][] BishopAttacks = new ulong[64][];

        static SlidingAttackTables()
        {
            for (int sq = 0; sq < 64; sq++)
            {
                RookMask[sq] = ComputeRookMask(sq);
                BishopMask[sq] = ComputeBishopMask(sq);
                RookRelevantBits[sq] = BitOperations.PopCount(RookMask[sq]);
                BishopRelevantBits[sq] = BitOperations.PopCount(BishopMask[sq]);

                int rsize = 1 << RookRelevantBits[sq];
                int bsize = 1 << BishopRelevantBits[sq];
                RookAttacks[sq] = new ulong[Math.Max(1, rsize)];
                BishopAttacks[sq] = new ulong[Math.Max(1, bsize)];

                // Precompute attack tables by enumerating all subsets of the mask
                var rookBits = MaskBits(RookMask[sq]);
                var bishopBits = MaskBits(BishopMask[sq]);

                for (int idx = 0; idx < rsize; idx++)
                {
                    ulong occ = BuildOccupancyFromIndex(idx, rookBits);
                    RookAttacks[sq][idx] = ComputeRookAttacksFromOcc(sq, occ);
                }

                for (int idx = 0; idx < bsize; idx++)
                {
                    ulong occ = BuildOccupancyFromIndex(idx, bishopBits);
                    BishopAttacks[sq][idx] = ComputeBishopAttacksFromOcc(sq, occ);
                }
            }
        }

        // Public getters: square is bit index 0..63
        public static ulong GetRookAttacks(int bitIndex, ulong occ)
        {
            var mask = RookMask[bitIndex];
            var bits = MaskBits(mask);
            int idx = CompressOccupancy(occ, bits);
            return RookAttacks[bitIndex][idx];
        }

        public static ulong GetBishopAttacks(int bitIndex, ulong occ)
        {
            var mask = BishopMask[bitIndex];
            var bits = MaskBits(mask);
            int idx = CompressOccupancy(occ, bits);
            return BishopAttacks[bitIndex][idx];
        }

        // Helpers
        private static int CompressOccupancy(ulong occ, int[] bits)
        {
            int idx = 0;
            for (int i = 0; i < bits.Length; i++)
            {
                if (((occ >> bits[i]) & 1UL) != 0UL) idx |= (1 << i);
            }
            return idx;
        }

        private static ulong BuildOccupancyFromIndex(int idx, int[] bits)
        {
            ulong occ = 0UL;
            for (int i = 0; i < bits.Length; i++)
            {
                if ((idx & (1 << i)) != 0) occ |= (1UL << bits[i]);
            }
            return occ;
        }

        private static int[] MaskBits(ulong mask)
        {
            int cnt = BitOperations.PopCount(mask);
            int[] bits = new int[cnt];
            int k = 0;
            while (mask != 0UL)
            {
                int b = BitOperations.TrailingZeroCount(mask);
                bits[k++] = b;
                mask &= mask - 1;
            }
            return bits;
        }

        // Compute rook mask: rays but excluding edge squares
        private static ulong ComputeRookMask(int sq)
        {
            ulong m = 0UL;
            int r = sq >> 3;
            int f = sq & 7;
            // north
            for (int rr = r + 1; rr <= 6; rr++) m |= 1UL << (rr * 8 + f);
            // south
            for (int rr = r - 1; rr >= 1; rr--) m |= 1UL << (rr * 8 + f);
            // east
            for (int ff = f + 1; ff <= 6; ff++) m |= 1UL << (r * 8 + ff);
            // west
            for (int ff = f - 1; ff >= 1; ff--) m |= 1UL << (r * 8 + ff);
            return m;
        }

        // Compute bishop mask: rays but excluding edge squares
        private static ulong ComputeBishopMask(int sq)
        {
            ulong m = 0UL;
            int r = sq >> 3;
            int f = sq & 7;
            // NE
            for (int rr = r + 1, ff = f + 1; rr <= 6 && ff <= 6; rr++, ff++) m |= 1UL << (rr * 8 + ff);
            // NW
            for (int rr = r + 1, ff = f - 1; rr <= 6 && ff >= 1; rr++, ff--) m |= 1UL << (rr * 8 + ff);
            // SE
            for (int rr = r - 1, ff = f + 1; rr >= 1 && ff <= 6; rr--, ff++) m |= 1UL << (rr * 8 + ff);
            // SW
            for (int rr = r - 1, ff = f - 1; rr >= 1 && ff >= 1; rr--, ff--) m |= 1UL << (rr * 8 + ff);
            return m;
        }

        // Compute attacks for a given occupancy (simple ray scans)
        private static ulong ComputeRookAttacksFromOcc(int sq, ulong occ)
        {
            ulong attacks = 0UL;
            int r = sq >> 3;
            int f = sq & 7;
            // north
            for (int rr = r + 1; rr <= 7; rr++)
            {
                int b = rr * 8 + f; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            // south
            for (int rr = r - 1; rr >= 0; rr--)
            {
                int b = rr * 8 + f; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            // east
            for (int ff = f + 1; ff <= 7; ff++)
            {
                int b = r * 8 + ff; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            // west
            for (int ff = f - 1; ff >= 0; ff--)
            {
                int b = r * 8 + ff; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            return attacks;
        }

        private static ulong ComputeBishopAttacksFromOcc(int sq, ulong occ)
        {
            ulong attacks = 0UL;
            int r = sq >> 3;
            int f = sq & 7;
            // NE
            for (int rr = r + 1, ff = f + 1; rr <= 7 && ff <= 7; rr++, ff++)
            {
                int b = rr * 8 + ff; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            // NW
            for (int rr = r + 1, ff = f - 1; rr <= 7 && ff >= 0; rr++, ff--)
            {
                int b = rr * 8 + ff; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            // SE
            for (int rr = r - 1, ff = f + 1; rr >= 0 && ff <= 7; rr--, ff++)
            {
                int b = rr * 8 + ff; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            // SW
            for (int rr = r - 1, ff = f - 1; rr >= 0 && ff >= 0; rr--, ff--)
            {
                int b = rr * 8 + ff; attacks |= 1UL << b; if (((occ >> b) & 1UL) != 0UL) break;
            }
            return attacks;
        }
    }
}
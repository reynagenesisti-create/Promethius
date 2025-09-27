using System;
using System.Runtime.CompilerServices;

namespace PromethiusEngine
{
    /// <summary>
    /// Very fast material-only evaluation with a tiny pawn-advance bonus.
    /// Returns White - Black in centipawns (positive => White is better).
    /// </summary>
    public static class Evaluator
    {
        // Basic piece values (centipawns)
        private const int PAWN = 100;
        private const int KNIGHT = 320;
        private const int BISHOP = 330;
        private const int ROOK = 500;
        private const int QUEEN = 900;
        private const int KING = 20000;

        // Small per-rank pawn bonus to encourage advancement (0..70)
        // This is optional and very cheap to compute.
        private static readonly int[] PawnAdvanceBonus = { 0, 5, 10, 15, 20, 25, 30, 40 };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Evaluate(Board board)
        {
            // Score = White - Black (centipawns)
            int score = 0;
            var squares = board.Squares;

            // iterate 0x88 board (0..127) skipping off-board squares
            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;

                sbyte p = squares[sq];
                if (p == Board.Empty) continue;

                // determine piece type and color
                bool isBlack = p > 6;
                int ptype = isBlack ? p - 6 : p;

                int val;
                switch (ptype)
                {
                    case Board.Pawn: val = PAWN; break;
                    case Board.Knight: val = KNIGHT; break;
                    case Board.Bishop: val = BISHOP; break;
                    case Board.Rook: val = ROOK; break;
                    case Board.Queen: val = QUEEN; break;
                    case Board.King: val = KING; break;
                    default: val = 0; break;
                }

                // color multiplier: +1 for White, -1 for Black
                int colMul = isBlack ? -1 : +1;

                // small pawn advancement bonus (cheap): use rank (0..7)
                if (ptype == Board.Pawn)
                {
                    int rank = sq >> 4; // rank: 0..7 (white's 1st rank = 0)
                    // White pawns more advanced when rank larger; black pawns reversed
                    int bonus = isBlack ? PawnAdvanceBonus[7 - rank] : PawnAdvanceBonus[rank];
                    score += colMul * (val + bonus);
                }
                else
                {
                    score += colMul * val;
                }
            }

            return score;
        }
    }
}

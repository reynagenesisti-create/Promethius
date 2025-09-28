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

        // Piece-square tables (midgame). Index 0 = a1 .. 63 = h8 (white perspective)
        // Simple, small tables intended as a lightweight improvement over pure material.
        private static readonly int[] PawnPST_MG =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            5, 10, 10, -20, -20, 10, 10, 5,
            5, -5, -10, 0, 0, -10, -5, 5,
            0, 0, 0, 20, 20, 0, 0, 0,
            5, 5, 10, 25, 25, 10, 5, 5,
            10, 10, 20, 30, 30, 20, 10, 10,
            50, 50, 50, 50, 50, 50, 50, 50,
            0, 0, 0, 0, 0, 0, 0, 0
        };

        private static readonly int[] PawnPST_EG =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            10, 10, 10, 0, 0, 10, 10, 10,
            5, 5, 5, 5, 5, 5, 5, 5,
            0, 0, 0, 10, 10, 0, 0, 0,
            5, 5, 10, 20, 20, 10, 5, 5,
            10, 10, 20, 30, 30, 20, 10, 10,
            50, 50, 50, 50, 50, 50, 50, 50,
            0, 0, 0, 0, 0, 0, 0, 0
        };

        private static readonly int[] KnightPST_MG =
        {
            -50,-40,-30,-30,-30,-30,-40,-50,
            -40,-20,0,5,5,0,-20,-40,
            -30,5,10,15,15,10,5,-30,
            -30,0,15,20,20,15,0,-30,
            -30,5,15,20,20,15,5,-30,
            -30,0,10,15,15,10,0,-30,
            -40,-20,0,0,0,0,-20,-40,
            -50,-40,-30,-30,-30,-30,-40,-50
        };

        private static readonly int[] BishopPST_MG =
        {
            -20,-10,-10,-10,-10,-10,-10,-20,
            -10,5,0,0,0,0,5,-10,
            -10,10,10,10,10,10,10,-10,
            -10,0,10,10,10,10,0,-10,
            -10,5,5,10,10,5,5,-10,
            -10,0,5,10,10,5,0,-10,
            -10,0,0,0,0,0,0,-10,
            -20,-10,-10,-10,-10,-10,-10,-20
        };

        private static readonly int[] RookPST_MG =
        {
            0,0,5,10,10,5,0,0,
            -5,0,0,0,0,0,0,-5,
            -5,0,0,0,0,0,0,-5,
            -5,0,0,0,0,0,0,-5,
            -5,0,0,0,0,0,0,-5,
            -5,0,0,0,0,0,0,-5,
            5,10,10,10,10,10,10,5,
            0,0,0,0,0,0,0,0
        };

        private static readonly int[] QueenPST_MG =
        {
            -20,-10,-10,-5,-5,-10,-10,-20,
            -10,0,5,0,0,5,0,-10,
            -10,5,5,5,5,5,5,-10,
            -5,0,5,5,5,5,0,-5,
            0,0,5,5,5,5,0,-5,
            -10,5,5,5,5,5,5,-10,
            -10,0,5,0,0,5,0,-10,
            -20,-10,-10,-5,-5,-10,-10,-20
        };

        private static readonly int[] KingPST_MG =
        {
            -30,-40,-40,-50,-50,-40,-40,-30,
            -30,-40,-40,-50,-50,-40,-40,-30,
            -30,-40,-40,-50,-50,-40,-40,-30,
            -30,-40,-40,-50,-50,-40,-40,-30,
            -20,-30,-30,-40,-40,-30,-30,-20,
            -10,-20,-20,-20,-20,-20,-20,-10,
            20,20,0,0,0,0,20,20,
            20,30,10,0,0,10,30,20
        };

        private static readonly int[] KingPST_EG =
        {
            -50,-40,-30,-20,-20,-30,-40,-50,
            -40,-20,0,5,5,0,-20,-40,
            -30,0,10,15,15,10,0,-30,
            -20,5,15,20,20,15,5,-20,
            -10,10,20,25,25,20,10,-10,
            0,10,20,25,25,20,10,0,
            10,20,20,20,20,20,20,10,
            20,30,10,0,0,10,30,20
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Evaluate(Board board)
        {
            // Score = White - Black (centipawns)
            int score = 0;
            var squares = board.Squares;

            // Compute endgame weight based on remaining material (phase).
            // Phase weights: N=1, B=1, R=2, Q=4. MaxPhase at start = 24 (both sides).
            int phase = 0;
            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;
                sbyte p = squares[sq];
                if (p == Board.Empty) continue;
                int ptype = (p > 6) ? p - 6 : p;
                switch (ptype)
                {
                    case Board.Knight: phase += 1; break;
                    case Board.Bishop: phase += 1; break;
                    case Board.Rook: phase += 2; break;
                    case Board.Queen: phase += 4; break;
                }
            }
            const int maxPhase = 24; // typical starting phase
            double egWeight = 1.0 - Math.Min(1.0, (double)phase / maxPhase);

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

                score += colMul * val;

                // piece-square table contribution
                int fromFile = sq & 0xF;
                int fromRank = sq >> 4;
                int idx = fromRank * 8 + fromFile; // 0..63
                int pstMg = 0;
                int pstEg = 0;

                switch (ptype)
                {
                    case Board.Pawn:
                        pstMg = PawnPST_MG[idx];
                        pstEg = PawnPST_EG[idx];
                        break;
                    case Board.Knight:
                        pstMg = KnightPST_MG[idx]; pstEg = pstMg; break;
                    case Board.Bishop:
                        pstMg = BishopPST_MG[idx]; pstEg = pstMg; break;
                    case Board.Rook:
                        pstMg = RookPST_MG[idx]; pstEg = pstMg; break;
                    case Board.Queen:
                        pstMg = QueenPST_MG[idx]; pstEg = pstMg; break;
                    case Board.King:
                        pstMg = KingPST_MG[idx]; pstEg = KingPST_EG[idx]; break;
                    default:
                        pstMg = 0; pstEg = 0; break;
                }

                // For black pieces, mirror the PST (white-perspective stored)
                if (isBlack)
                {
                    int mirror = 63 - idx;
                    switch (ptype)
                    {
                        case Board.Pawn:
                            pstMg = PawnPST_MG[mirror]; pstEg = PawnPST_EG[mirror]; break;
                        case Board.Knight:
                            pstMg = KnightPST_MG[mirror]; pstEg = pstMg; break;
                        case Board.Bishop:
                            pstMg = BishopPST_MG[mirror]; pstEg = pstMg; break;
                        case Board.Rook:
                            pstMg = RookPST_MG[mirror]; pstEg = pstMg; break;
                        case Board.Queen:
                            pstMg = QueenPST_MG[mirror]; pstEg = pstMg; break;
                        case Board.King:
                            pstMg = KingPST_MG[mirror]; pstEg = KingPST_EG[mirror]; break;
                    }
                }

                int pstVal = (int)Math.Round((1.0 - egWeight) * pstMg + egWeight * pstEg);
                score += colMul * pstVal;
            }

            return score;
        }
    }
}

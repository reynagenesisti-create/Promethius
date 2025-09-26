using System;

namespace PromethiusEngine
{
    public static class Evaluate
    {
        // Simple material-only evaluation (white positive)
        // Returns score in centipawns from White's perspective.
        public static int EvaluateMaterial(Board board)
        {
            var squares = board.Squares;
            int score = 0;
            for (int s = 0; s < 128; s++)
            {
                if ((s & 0x88) != 0) continue;
                sbyte p = squares[s];
                if (p == Board.Empty) continue;
                int color = (p > 6) ? Board.Black : Board.White;
                int ptype = (p > 6) ? p - 6 : p;
                int val = ptype switch
                {
                    Board.Pawn => 100,
                    Board.Knight => 320,
                    Board.Bishop => 330,
                    Board.Rook => 500,
                    Board.Queen => 900,
                    Board.King => 20000,
                    _ => 0
                };
                score += (color == Board.White) ? val : -val;
            }
            return score;
        }
    }
}

using System;

namespace PromethiusEngine
{
    public static class Search
    {
        private static readonly Random s_rng = new Random();

        // Temporary stub: return a random legal move from the position (or 0 if none)
        public static Board.Move SearchBestMove(Board board, int maxDepth, int thinkMs)
        {
            // Use the fast span-based generator to avoid allocations
            Span<Board.Move> moves = stackalloc Board.Move[218];
            MoveGenerator.GenerateMovesSpan(board, moves, out int count);
            if (count == 0) return (Board.Move)0;
            int idx = s_rng.Next(count);
            return moves[idx];
        }
    }
}

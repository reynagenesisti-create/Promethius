using System;

namespace PromethiusEngine
{
    public static class Search
    {
        private static readonly Random s_rng = new Random();
        private const int MATE_SCORE = 1000000;

        // Public entry: find best move using a simple depth-limited negamax (no alpha-beta)
        // Returns move 0 when no legal move is found.
        public static Board.Move SearchBestMove(Board board, int maxDepth, int thinkMs)
        {
            Span<Board.Move> moves = stackalloc Board.Move[218];
            MoveGenerator.GenerateMovesSpan(board, moves, out int count);
            if (count == 0) return (Board.Move)0;

            Board.Move bestMove = (Board.Move)0;
            int bestScore = int.MinValue + 1;

            for (int i = 0; i < count; i++)
            {
                var mv = moves[i];
                board.MakeMoveWithUndo(mv, out var undo);
                int score = -Negamax(board, maxDepth - 1);
                board.UnmakeMove(mv, undo);

                if (score > bestScore || (score == bestScore && s_rng.Next(2) == 0))
                {
                    bestScore = score;
                    bestMove = mv;
                }
            }

            return bestMove;
        }

        // Negamax returns score from side-to-move perspective
        private static int Negamax(Board board, int depth)
        {
            // Terminal/draw detection
            if (MoveGenerator.IsInCheckmate(board))
            {
                // side to move is checkmated: worst score
                return -MATE_SCORE - depth;
            }
            if (MoveGenerator.IsDraw(board, null))
            {
                return 0; // draw
            }

            if (depth == 0)
            {
                // Evaluate from White perspective; convert to side-to-move perspective
                int mat = Evaluate.EvaluateMaterial(board);
                return (board.SideToMove == Board.White) ? mat : -mat;
            }

            Span<Board.Move> moves = stackalloc Board.Move[218];
            MoveGenerator.GenerateMovesSpan(board, moves, out int count);

            if (count == 0)
            {
                // No legal moves: either stalemate or checkmate handled earlier, but handle defensively
                if (MoveGenerator.IsInCheck(board)) return -MATE_SCORE - depth;
                return 0;
            }

            int best = int.MinValue + 1;
            for (int i = 0; i < count; i++)
            {
                var mv = moves[i];
                board.MakeMoveWithUndo(mv, out var undo);
                int val = -Negamax(board, depth - 1);
                board.UnmakeMove(mv, undo);
                if (val > best) best = val;
            }

            return best;
        }
    }
}

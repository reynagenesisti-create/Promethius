using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace PromethiusEngine
{
    public static class Search
    {
        // stop controls (used by UCI thread)
        private static volatile bool s_stopRequested = false;
        private static DateTime s_stopTime = DateTime.MaxValue;

        // statistics
        private static long s_nodes = 0;

        // values
        private const int MATE_SCORE = 1000000;
        private const int MAX_SCORE = 2000000;

        public static void ClearStop()
        {
            s_stopRequested = false;
            s_stopTime = DateTime.MaxValue;
            s_nodes = 0;
        }

        public static void RequestStop()
        {
            s_stopRequested = true;
            s_stopTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Search best move using iterative deepening and pure negamax (no alpha-beta).
        /// thinkMs is the maximum thinking time in milliseconds (best-effort).
        /// Returns Board.Move (0 if none found).
        /// </summary>
        public static Board.Move SearchBestMove(Board rootBoard, int maxDepth, int thinkMs)
        {
            if (rootBoard == null) throw new ArgumentNullException(nameof(rootBoard));
            if (maxDepth <= 0) maxDepth = 1;

            // deadline
            if (thinkMs > 0)
            {
                s_stopTime = DateTime.UtcNow.AddMilliseconds(thinkMs);
                s_stopRequested = false;
            }
            else
            {
                s_stopTime = DateTime.MaxValue;
                s_stopRequested = false;
            }

            s_nodes = 0;
            var sw = Stopwatch.StartNew();
            Board.Move bestMoveSoFar = 0;
            int bestScoreSoFar = int.MinValue + 1024;

            try
            {
                // Iterative deepening from 1..maxDepth
                for (int depth = 1; depth <= maxDepth; depth++)
                {
                    // time/cancel check before each depth
                    CheckStop();

                    // run a PV-building negamax to get a principal variation for this depth
                    var (rootScore, rootPv) = NegamaxWithPv(rootBoard, depth, -MAX_SCORE, MAX_SCORE);
                    if (rootPv != null && rootPv.Count > 0)
                    {
                        bestMoveSoFar = rootPv[0];
                        bestScoreSoFar = rootScore;
                    }

                    // Print UCI info line for this completed depth: depth, time, nodes, nps, pv
                    long elapsedMs = sw.ElapsedMilliseconds;
                    long nodes = s_nodes;
                    long nps = elapsedMs > 0 ? (nodes * 1000L) / elapsedMs : nodes;
                    string pvStr = "";
                    if (rootPv != null && rootPv.Count > 0)
                    {
                        // build PV string
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < rootPv.Count; i++)
                        {
                            if (i > 0) sb.Append(' ');
                            sb.Append(MoveToUci(rootPv[i]));
                        }
                        pvStr = sb.ToString();
                    }
                    Console.WriteLine($"info depth {depth} time {elapsedMs} nodes {nodes} nps {nps}" + (pvStr.Length > 0 ? $" pv {pvStr}" : ""));

                    // If we found a mate we can stop early.
                    if (Math.Abs(bestScoreSoFar) >= MATE_SCORE - 1000) break;

                    // stop if time expired
                    if (s_stopRequested || DateTime.UtcNow >= s_stopTime) break;
                }
            }
            catch (OperationCanceledException)
            {
                // search stopped by request: return best found so far
            }

            return bestMoveSoFar;
        }

        // Do a root-level scan to pick the best move at the root (so we can return a move)
        // This performs the same negamax child calls (depth-1) but returns move->score mapping.
        private static (Board.Move, int) RootBestMoveForDepth(Board board, int depth)
        {
            // Use the PV-building negamax at root and return its first move and score
            var (score, pv) = NegamaxWithPv(board, depth, -MAX_SCORE, MAX_SCORE);
            if (pv == null || pv.Count == 0) return (0, EvaluateLeafTerminal(board, depth));
            return (pv[0], score);
        }

        // Build a principal variation-aware negamax (returns score and PV as a list of moves).
        // This is more expensive than the pure score-only Negamax but allows printing PV.
        private static (int score, List<Board.Move> pv) NegamaxWithPv(Board board, int depth, int alpha, int beta)
        {
            CheckStop();

            s_nodes++;

            if (depth == 0)
            {
                return (EvaluateLeaf(board), new List<Board.Move>());
            }

            // Generate legal moves
            Span<Board.Move> moves = stackalloc Board.Move[218];
            MoveGenerator.GenerateMovesSpan(board, moves, out int movesCount);

            if (movesCount == 0)
            {
                if (MoveGenerator.IsInCheckmate(board)) return (-MATE_SCORE - depth, new List<Board.Move>());
                return (0, new List<Board.Move>());
            }

            int best = int.MinValue + 1024;
            List<Board.Move> bestPv = new List<Board.Move>();

            for (int i = 0; i < movesCount; i++)
            {
                CheckStop();

                Board.Move mv = moves[i];
                board.MakeMoveWithUndo(mv, out var undo);
                int val;
                List<Board.Move> childPv;
                try
                {
                    var child = NegamaxWithPv(board, depth - 1, -beta, -alpha);
                    val = -child.score;
                    childPv = child.pv;
                }
                finally
                {
                    board.UnmakeMove(mv, undo);
                }

                if (val > best)
                {
                    best = val;
                    bestPv = new List<Board.Move>(1 + (childPv?.Count ?? 0));
                    bestPv.Add(mv);
                    if (childPv != null && childPv.Count > 0) bestPv.AddRange(childPv);
                }

                if (best > alpha) alpha = best;
                if (alpha >= beta) break;
            }

            return (best, bestPv);
        }

        // Helper: convert a Board.Move to UCI string (e2e4, e7e8q etc.)
        private static string MoveToUci(Board.Move mv)
        {
            int from = Board.Move.GetFrom(mv);
            int to = Board.Move.GetTo(mv);
            int promo = Board.Move.GetPromotion(mv);

            int fromFile = from & 0xF;
            int fromRank = from >> 4;
            int toFile = to & 0xF;
            int toRank = to >> 4;

            char ffile = (char)('a' + fromFile);
            char frank = (char)('1' + fromRank);
            char tfile = (char)('a' + toFile);
            char trank = (char)('1' + toRank);

            if (promo == 0) return $"{ffile}{frank}{tfile}{trank}";
            char p = promo switch { 2 => 'n', 3 => 'b', 4 => 'r', 5 => 'q', _ => 'q' };
            return $"{ffile}{frank}{tfile}{trank}{p}";
        }

        // Negamax with alpha-beta pruning: returns score from the perspective of side-to-move.
        // alpha and beta are window bounds (inclusive/exclusive) in centipawns.
        private static int Negamax(Board board, int depth, int alpha, int beta)
        {
            CheckStop();

            s_nodes++;

            // Terminal checks / leaf evaluation
            if (depth == 0)
            {
                return EvaluateLeaf(board);
            }

            // Generate legal moves
            Span<Board.Move> moves = stackalloc Board.Move[218];
            MoveGenerator.GenerateMovesSpan(board, moves, out int movesCount);

            // No legal moves: checkmate or stalemate
            if (movesCount == 0)
            {
                if (MoveGenerator.IsInCheckmate(board)) return -MATE_SCORE - depth; // prefer shorter mates
                return 0; // stalemate -> draw
            }

            int best = int.MinValue + 1024;

            for (int i = 0; i < movesCount; i++)
            {
                CheckStop();

                Board.Move mv = moves[i];
                board.MakeMoveWithUndo(mv, out var undo);
                int val;
                try
                {
                    // Negate and swap alpha/beta for the child node (negamax transformation)
                    val = -Negamax(board, depth - 1, -beta, -alpha);
                }
                finally
                {
                    board.UnmakeMove(mv, undo);
                }

                if (val > best) best = val;
                // update alpha and do beta-cutoff
                if (best > alpha) alpha = best;
                if (alpha >= beta)
                {
                    // cutoff
                    break;
                }
            }

            return best;
        }

        // Evaluate leaf node in a consistent way (score from side-to-move perspective).
        // Uses Evaluate.Evaluate(board) which is expected to be White - Black in centipawns.
        private static int EvaluateLeaf(Board board)
        {
            int evalWhiteMinusBlack = Evaluator.Evaluate(board);
            // Return score from perspective of side to move
            return (board.SideToMove == Board.White) ? evalWhiteMinusBlack : -evalWhiteMinusBlack;
        }

        // Called when no legal moves at root or leaf to produce consistent terminal score
        private static int EvaluateLeafTerminal(Board board, int depth)
        {
            if (MoveGenerator.IsInCheckmate(board)) return -MATE_SCORE - depth;
            return 0;
        }

        private static void CheckStop()
        {
            if (s_stopRequested) throw new OperationCanceledException();
            if (DateTime.UtcNow >= s_stopTime)
            {
                s_stopRequested = true;
                throw new OperationCanceledException();
            }
        }

        // Expose nodes for diagnostics if desired
        public static long NodesSearched => s_nodes;
    }
}

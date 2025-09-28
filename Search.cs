using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PromethiusEngine
{
    public static class Search
    {
        // stop controls (used by UCI thread)
        private static volatile bool s_stopRequested = false;
        private static DateTime s_stopTime = DateTime.MaxValue;

        // statistics
        private static long s_nodes = 0;

        // move ordering structures
        // killers per ply (two killers per depth)
        private static readonly int[,] s_killers = new int[128, 2];
        // history heuristic: map move (int) -> score
        private static readonly System.Collections.Generic.Dictionary<int, int> s_history = new System.Collections.Generic.Dictionary<int, int>();
        // PV from previous iterative-deepening depth: try this at root first
        private static List<Board.Move> s_lastPv = new List<Board.Move>();
        private static int s_currentRootDepth = 0;
        private static Board.Move s_rootPreferredMove = 0;

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
                    // set root PV hints for ordering
                    s_currentRootDepth = depth;
                    s_rootPreferredMove = s_lastPv != null && s_lastPv.Count > 0 ? s_lastPv[0] : (Board.Move)0;

                    var (rootScore, rootPv) = NegamaxWithPv(rootBoard, depth, -MAX_SCORE, MAX_SCORE);

                    // remember PV for next iteration
                    s_lastPv = rootPv ?? new List<Board.Move>();
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
                // At leaf, run quiescence search to avoid horizon effects
                int qscore = Quiescence(board, alpha, beta);
                return (qscore, new List<Board.Move>());
            }

            // Generate legal moves
            Span<Board.Move> moves = stackalloc Board.Move[218];
            MoveGenerator.GenerateMovesSpan(board, moves, out int movesCount);

            // probe transposition table for best move to try first
            int ttMoveInt = 0;
            if (TranspositionTable.Probe(board.ZobristKey, out int ttValue, out int ttDepth, out int ttMove, out byte ttFlags))
            {
                ttMoveInt = ttMove;
            }

            // Order moves in-place into a small array of pairs (move, score)
            var ordered = OrderMoves(moves.Slice(0, movesCount), board, ttMoveInt, depth);

            if (movesCount == 0)
            {
                if (MoveGenerator.IsInCheckmate(board)) return (-MATE_SCORE - depth, new List<Board.Move>());
                return (0, new List<Board.Move>());
            }

            int best = int.MinValue + 1024;
            List<Board.Move> bestPv = new List<Board.Move>();

            for (int i = 0; i < ordered.Length; i++)
            {
                CheckStop();

                Board.Move mv = ordered[i].move;
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
                if (alpha >= beta)
                {
                    // store killer / history for non-capture moves
                    int mvInt = (int)mv;
                    bool isCapture = ((mvInt >> 17) & 1) != 0;
                    if (!isCapture)
                    {
                        // killers: store up to 2 per ply (use depth as index)
                        int ply = depth; if (ply < 0) ply = 0; if (ply >= 128) ply = 127;
                        if (s_killers[ply, 0] == mvInt) { }
                        else if (s_killers[ply, 0] == 0) s_killers[ply, 0] = mvInt;
                        else if (s_killers[ply, 1] == 0) s_killers[ply, 1] = mvInt;
                        else { s_killers[ply, 1] = s_killers[ply, 0]; s_killers[ply, 0] = mvInt; }

                        // history increment
                        if (!s_history.TryGetValue(mvInt, out int h)) h = 0;
                        s_history[mvInt] = h + (depth * depth);
                    }

                    // store to transposition table as lower bound
                    TranspositionTable.Store(board.ZobristKey, best, depth, TranspositionTable.FlagLower, (int)mv);

                    break;
                }
            }

            return (best, bestPv);
        }

        // Simple move ordering: returns array of (move, score) sorted descending
        private static (Board.Move move, int score)[] OrderMoves(Span<Board.Move> moves, Board board, int ttMoveInt, int depth)
        {
            int n = moves.Length;
            var arr = new (Board.Move move, int score)[n];
            for (int i = 0; i < n; i++)
            {
                int mvInt = (int)moves[i];
                int score = 0;
                // TT move gets high priority
                if (mvInt == ttMoveInt) score += 1000000;
                // PV root preferred move
                if (s_currentRootDepth > 0 && mvInt == (int)s_rootPreferredMove) score += 900000;
                // captures boosted (cheap MVV-LVA approximation: prefer capturing higher piece types)
                bool isCapture = ((mvInt >> 17) & 1) != 0;
                if (isCapture)
                {
                    int to = (mvInt >> 7) & 127;
                    int from = mvInt & 127;
                    // determine victim and attacker piece types
                    sbyte vict = board.Squares[to];
                    sbyte att = board.Squares[from];
                    int victType = vict == Board.Empty ? 0 : (vict > 6 ? vict - 6 : vict);
                    int attType = att == Board.Empty ? 0 : (att > 6 ? att - 6 : att);
                    int VictVal = 0; int AttVal = 0;
                    switch (victType) { case Board.Pawn: VictVal = 100; break; case Board.Knight: VictVal = 320; break; case Board.Bishop: VictVal = 330; break; case Board.Rook: VictVal = 500; break; case Board.Queen: VictVal = 900; break; case Board.King: VictVal = 20000; break; }
                    switch (attType) { case Board.Pawn: AttVal = 100; break; case Board.Knight: AttVal = 320; break; case Board.Bishop: AttVal = 330; break; case Board.Rook: AttVal = 500; break; case Board.Queen: AttVal = 900; break; case Board.King: AttVal = 20000; break; }
                    // MVV-LVA style: prefer capturing more valuable victim with less valuable attacker
                    score += 600000 + (VictVal * 10 - AttVal);
                }
                // killer moves
                for (int k = 0; k < 2; k++) if (s_killers[depth < 128 ? depth : 127, k] == mvInt) score += 200000;
                // history heuristic
                if (s_history.TryGetValue(mvInt, out int hscore)) score += Math.Min(hscore, 100000);

                // promotions: small bonus
                int promo = (mvInt >> 14) & 7;
                if (promo != 0) score += 300000;

                arr[i] = (moves[i], score);
            }

            Array.Sort(arr, (a, b) => b.score.CompareTo(a.score));
            return arr;
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
                // call quiescence search at leaves
                return Quiescence(board, alpha, beta);
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

            // probe TT for ordering
            int ttMoveInt = 0;
            if (TranspositionTable.Probe(board.ZobristKey, out int ttValue, out int ttDepth, out int ttMove, out byte ttFlags)) ttMoveInt = ttMove;

            var ordered = OrderMoves(moves.Slice(0, movesCount), board, ttMoveInt, depth);

            for (int i = 0; i < ordered.Length; i++)
            {
                CheckStop();
                Board.Move mv = ordered[i].move;
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
                    // store killer / history for non-capture
                    int mvInt = (int)mv;
                    bool isCapture = ((mvInt >> 17) & 1) != 0;
                    if (!isCapture)
                    {
                        int ply = depth; if (ply < 0) ply = 0; if (ply >= 128) ply = 127;
                        if (s_killers[ply, 0] == mvInt) { }
                        else if (s_killers[ply, 0] == 0) s_killers[ply, 0] = mvInt;
                        else if (s_killers[ply, 1] == 0) s_killers[ply, 1] = mvInt;
                        else { s_killers[ply, 1] = s_killers[ply, 0]; s_killers[ply, 0] = mvInt; }

                        if (!s_history.TryGetValue(mvInt, out int h)) h = 0;
                        s_history[mvInt] = h + (depth * depth);
                    }

                    // TT store lower bound
                    TranspositionTable.Store(board.ZobristKey, best, depth, TranspositionTable.FlagLower, (int)mv);

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

        // Quiescence search: explore captures (and promotions) until a quiet position.
        // Uses a simple stand-pat pruning: if standPat >= beta -> fail-high; otherwise search captures.
        private static int Quiescence(Board board, int alpha, int beta)
        {
            CheckStop();

            s_nodes++;

            // Stand-pat: static evaluation from side-to-move perspective
            int standPat = EvaluateLeaf(board);
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;

            // Generate only captures/promotions to extend the search
            var caps = MoveGenerator.GenerateCaptures(board);
            if (caps == null || caps.Count == 0) return standPat;

            // Order captures using MVV-LVA via OrderMoves
            Span<Board.Move> capSpan = caps.Count > 0 ? CollectionsMarshal.AsSpan(caps) : Span<Board.Move>.Empty;
            var orderedCaps = OrderMoves(capSpan, board, 0, 0);
            for (int i = 0; i < orderedCaps.Length; i++)
            {
                CheckStop();

                Board.Move mv = orderedCaps[i].move;

                // make move
                board.MakeMoveWithUndo(mv, out var undo);
                int score;
                try
                {
                    // recursive quiescence: note negamax perspective
                    score = -Quiescence(board, -beta, -alpha);
                }
                finally
                {
                    board.UnmakeMove(mv, undo);
                }

                if (score >= beta) return beta;
                if (score > alpha) alpha = score;
            }

            return alpha;
        }
    }
}

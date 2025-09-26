using System;
using System.Collections.Generic;
using System.Numerics;

namespace PromethiusEngine
{
    public static class MoveGenerator
    {
        // Enable deep diagnostics (set by Program during perft runs)
        public static bool DiagnosticsEnabled = false;

        // Reusable buffers to avoid per-node allocations (single-threaded engine)
        private static readonly bool[] s_oppAttacked = new bool[128];
        private static readonly int[] s_pinDir = new int[128];
        private static readonly int[] s_checkersArr = new int[8];
        private static int s_checkersCount = 0;
        private static readonly bool[] s_blockSquares = new bool[128];
        // stamped block squares to avoid clearing a 128-array each node
        private static readonly int[] s_blockStamp = new int[128];
        private static int s_blockGen = 1;

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
            char pchar = promo switch { 2 => 'n', 3 => 'b', 4 => 'r', 5 => 'q', _ => '?' };
            return $"{ffile}{frank}{tfile}{trank}{pchar}";
        }
        // 0x88 deltas
        private static readonly int[] KnightDeltas = { 0x21, 0x1f, 0x12, 0x0e, -0x21, -0x1f, -0x12, -0x0e };
        private static readonly int[] KingDeltas = { 0x10, 0x11, 0x01, -0x0f, -0x10, -0x11, -0x01, 0x0f };
        private static readonly int[] BishopDeltas = { 0x11, 0x0f, -0x11, -0x0f };
        private static readonly int[] RookDeltas = { 0x10, 0x01, -0x10, -0x01 };
        private static readonly int[] QueenDeltas = { 0x10, 0x11, 0x01, 0x0f, -0x10, -0x11, -0x01, -0x0f };

        // Bitboard helpers: map 0x88 square to bit index (0..63): index = rank*8 + file
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int SqToBitIndex(int sq) => ((sq >> 4) * 8) + (sq & 0xF);

        // Build bitboards for occupancy and pawns. bit 0 = a1, bit 63 = h8
        private static void BuildPawnAndOccupancyBitboards(Board board, out ulong occ, out ulong whitePawns, out ulong blackPawns)
        {
            occ = 0UL; whitePawns = 0UL; blackPawns = 0UL;
            var squares = board.Squares;
            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;
                sbyte p = squares[sq];
                if (p == Board.Empty) continue;
                int idx = SqToBitIndex(sq);
                occ |= (1UL << idx);
                if (p == Board.Pawn) whitePawns |= (1UL << idx);
                else if (p == Board.Pawn + 6) blackPawns |= (1UL << idx);
            }
        }

        // Lightweight move buffer to avoid List allocations in perft inner loops
        private struct MoveBuffer
        {
            public Board.Move[] Items;
            public int Count;
            public MoveBuffer(int capacity) { Items = new Board.Move[Math.Max(4, capacity)]; Count = 0; }
            public void Clear() { Count = 0; }
            private void EnsureCapacity(int min)
            {
                if (Items.Length < min) Array.Resize(ref Items, Math.Max(Items.Length * 2, min));
            }
            public void Add(Board.Move mv) { if (Count >= Items.Length) EnsureCapacity(Count + 1); Items[Count++] = mv; }
            public Board.Move this[int i] => Items[i];
        }

        // Public: generate all legal moves into provided list (clears it)
        public static void GenerateMoves(Board board, List<Board.Move> moves)
        {
            // Backwards-compatible wrapper: use the new buffer generator and copy into list
            moves.Clear();
            var buf = new MoveBuffer(128);
            GenerateMovesBuffer(board, ref buf);
            for (int i = 0; i < buf.Count; i++) moves.Add(buf[i]);
            return;
        }

        // New high-performance API: write moves into a caller-provided Span<Board.Move>.
        // outCount is set to the number of moves written. The span should be large
        // enough to hold the position's legal moves (218 max in chess).
        public static void GenerateMovesSpan(Board board, Span<Board.Move> outMoves, out int outCount)
        {
            // Use internal buffer generator and then copy into outMoves
            var buf = new MoveBuffer(218);
            GenerateMovesBuffer(board, ref buf);
            outCount = buf.Count;
            if (outCount > outMoves.Length) throw new ArgumentException("outMoves span too small");
            for (int i = 0; i < outCount; i++) outMoves[i] = buf[i];
        }

        // New: generate moves into a MoveBuffer (avoids List allocs)
        private static void GenerateMovesBuffer(Board board, ref MoveBuffer moves)
        {
            moves.Clear();
            // local alias so we can capture it in local functions (can't capture ref params)
            var outBuf = moves;
            outBuf.Clear();
            var squares = board.Squares;

            int stm = board.SideToMove;
            int opp = stm == Board.White ? Board.Black : Board.White;
            int myKingSq = (stm == Board.White) ? board.WhiteKingSquare : board.BlackKingSquare;

            // reuse static buffers to avoid allocations
            ComputeOpponentAttacksPins(board, stm, myKingSq, s_oppAttacked, s_pinDir, s_checkersArr, out s_checkersCount);
            var oppAttacked = s_oppAttacked;
            var pinDir = s_pinDir;
            var checkersCount = s_checkersCount;

            if (checkersCount >= 2)
            {
                // only king moves: generate directly into the main output buffer to avoid temporary allocations
                GenerateKingLegalMovesBuffer(board, myKingSq, stm, oppAttacked, ref outBuf);
                // write back and return
                moves = outBuf;
                return;
            }

            // reuse blockSquares buffer via stamping to avoid O(128) clear
            s_blockGen++;
            if (s_blockGen == 0) { s_blockGen = 1; Array.Clear(s_blockStamp, 0, 128); }
            if (checkersCount == 1)
            {
                int checkerSq = s_checkersArr[0];
                int dir = FindDirection(checkerSq, myKingSq);
                if (dir != 0)
                {
                    int s = checkerSq;
                    while ((s & 0x88) == 0)
                    {
                        s_blockStamp[s] = s_blockGen;
                        if (s == myKingSq) break;
                        s += dir;
                    }
                }
                else s_blockStamp[checkerSq] = s_blockGen;
            }

            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;
                sbyte piece = squares[sq];
                if (piece == Board.Empty) continue;
                int color = (piece > 6) ? Board.Black : Board.White;
                if (color != stm) continue;
                int ptype = (piece > 6) ? piece - 6 : piece;

                if (ptype == Board.King)
                {
                    // Generate king moves straight into the main buffer to avoid allocating a small temp buffer
                    GenerateKingLegalMovesBuffer(board, sq, stm, oppAttacked, ref outBuf);
                    continue;
                }

                bool isPinned = pinDir[sq] != 0;
                int pd = pinDir[sq];

                // Inline pseudo-move generation for each piece type to keep hot path tight (no delegates)
                if (ptype == Board.Pawn)
                {
                    var squaresLocal = squares;
                    int dir = color == Board.White ? 0x10 : -0x10;
                    int startRank = color == Board.White ? 1 : 6;
                    int promoteRank = color == Board.White ? 6 : 1;
                    int epSq = board.EnPassant;

                    int to = sq + dir;
                    if ((to & 0x88) == 0 && squaresLocal[to] == Board.Empty)
                    {
                        if ((sq >> 4) == promoteRank)
                        {
                            for (int p = 2; p <= 5; p++)
                            {
                                var mv = Board.Move.Create(sq, to, p, false, false, false, false);
                                // no ep here
                                if (isPinned && !MoveIsOnPinLine(sq, to, pd)) { }
                                else if (checkersCount == 1 && s_blockStamp[to] != s_blockGen) { }
                                else outBuf.Add(mv);
                            }
                        }
                        else
                        {
                            var mv = Board.Move.Create(sq, to, 0, false, false, false, false);
                            if (!(isPinned && !MoveIsOnPinLine(sq, to, pd)) && !(checkersCount == 1 && s_blockStamp[to] != s_blockGen)) outBuf.Add(mv);
                            int dbl = sq + dir * 2;
                            if ((sq >> 4) == startRank && (dbl & 0x88) == 0 && squaresLocal[dbl] == Board.Empty)
                            {
                                var mv2 = Board.Move.Create(sq, dbl, 0, false, false, false, true);
                                if (!(isPinned && !MoveIsOnPinLine(sq, dbl, pd)) && !(checkersCount == 1 && s_blockStamp[dbl] != s_blockGen)) outBuf.Add(mv2);
                            }
                        }
                    }

                    for (int dx = -1; dx <= 1; dx += 2)
                    {
                        int cap = sq + dir + dx;
                        if ((cap & 0x88) != 0) continue;
                        sbyte target = squares[cap];
                        if (target != Board.Empty)
                        {
                            int tcolor = (target > 6) ? Board.Black : Board.White;
                            if (tcolor != color)
                            {
                                if ((sq >> 4) == promoteRank)
                                {
                                    for (int p = 2; p <= 5; p++)
                                    {
                                        var mv = Board.Move.Create(sq, cap, p, true, false, false, false);
                                        if (!(isPinned && !MoveIsOnPinLine(sq, cap, pd)) && !(checkersCount == 1 && s_blockStamp[cap] != s_blockGen)) outBuf.Add(mv);
                                    }
                                }
                                else
                                {
                                    var mv = Board.Move.Create(sq, cap, 0, true, false, false, false);
                                    if (!(isPinned && !MoveIsOnPinLine(sq, cap, pd)) && !(checkersCount == 1 && s_blockStamp[cap] != s_blockGen)) outBuf.Add(mv);
                                }
                            }
                        }

                        if (cap == epSq)
                        {
                            int epRankCheck = color == Board.White ? 4 : 3;
                            if ((sq >> 4) == epRankCheck && squares[sq] == (color == Board.White ? Board.Pawn : (sbyte)(Board.Pawn + 6)))
                            {
                                var mv = Board.Move.Create(sq, cap, 0, true, true, false, false);
                                // ep requires simulation
                                board.MakeMoveWithUndo(mv, out var undo);
                                int newKing = (stm == Board.White) ? board.WhiteKingSquare : board.BlackKingSquare;
                                bool kingInCheck = IsSquareAttacked(board, newKing, opp);
                                UnmakeMove(board, mv, undo);
                                if (!kingInCheck) outBuf.Add(mv);
                            }
                        }
                    }
                }
                else if (ptype == Board.Knight)
                {
                    var squaresLocal = squares;
                    foreach (var d in KnightDeltas)
                    {
                        int to = sq + d;
                        if ((to & 0x88) != 0) continue;
                        sbyte t = squaresLocal[to];
                        if (t == Board.Empty)
                        {
                            if (!(isPinned && !MoveIsOnPinLine(sq, to, pd)) && !(checkersCount == 1 && s_blockStamp[to] != s_blockGen)) outBuf.Add(Board.Move.Create(sq, to, 0, false, false, false, false));
                        }
                        else { int tcol = (t > 6) ? Board.Black : Board.White; if (tcol != color) { if (!(isPinned && !MoveIsOnPinLine(sq, to, pd)) && !(checkersCount == 1 && s_blockStamp[to] != s_blockGen)) outBuf.Add(Board.Move.Create(sq, to, 0, true, false, false, false)); } }
                    }
                }
                else if (ptype == Board.Bishop || ptype == Board.Rook || ptype == Board.Queen)
                {
                    var squaresLocal = squares;
                    int[] dirs = (ptype == Board.Bishop) ? BishopDeltas : (ptype == Board.Rook ? RookDeltas : QueenDeltas);
                    foreach (var d in dirs)
                    {
                        int t = sq + d;
                        while ((t & 0x88) == 0)
                        {
                            sbyte occ = squaresLocal[t];
                            if (occ == Board.Empty)
                            {
                                if (!(isPinned && !MoveIsOnPinLine(sq, t, pd)) && !(checkersCount == 1 && s_blockStamp[t] != s_blockGen)) outBuf.Add(Board.Move.Create(sq, t, 0, false, false, false, false));
                            }
                            else
                            {
                                int tcol = (occ > 6) ? Board.Black : Board.White;
                                if (tcol != color) if (!(isPinned && !MoveIsOnPinLine(sq, t, pd)) && !(checkersCount == 1 && s_blockStamp[t] != s_blockGen)) outBuf.Add(Board.Move.Create(sq, t, 0, true, false, false, false));
                                break;
                            }
                            t += d;
                        }
                    }
                }
            }

            // castling: generate directly into the main buffer to avoid temp allocation
            GenerateLegalCastlingBuffer(board, stm, oppAttacked, ref outBuf);

            // write back to ref param
            moves = outBuf;
        }

        // ----------------------- Helpers -----------------------

        // Build opponent attack map, pinned pieces and checkers (against myColor's king)
        // Fills provided buffers (to avoid allocations). Buffers must be 128-length arrays and checkersArr an int[]; count returned via out param.
        private static void ComputeOpponentAttacksPins(Board board, int myColor, int myKingSq, bool[] oppAttackedBuf, int[] pinDirBuf, int[] checkersArr, out int checkersCount)
        {
            var squares = board.Squares;
            // clear buffers (only the used slots will be written but clear to be safe)
            Array.Clear(oppAttackedBuf, 0, 128);
            Array.Clear(pinDirBuf, 0, 128);
            checkersCount = 0;
            int oppColor = (myColor == Board.White) ? Board.Black : Board.White;

            // 1) Mark pawn/knight/king attacks and sliding attacks from opponent pieces to produce oppAttacked.
            for (int s = 0; s < 128; s++)
            {
                if ((s & 0x88) != 0) continue;
                sbyte p = squares[s];
                if (p == Board.Empty) continue;
                int color = (p > 6) ? Board.Black : Board.White;
                if (color != oppColor) continue;

                int ptype = (p > 6) ? p - 6 : p;

                switch (ptype)
                {
                    case Board.Pawn:
                        // White pawns attack s+15 and s+17, black pawns attack s-15 and s-17
                        if (oppColor == Board.White)
                        {
                            MarkIfOnboard(oppAttackedBuf, s + 15);
                            MarkIfOnboard(oppAttackedBuf, s + 17);
                        }
                        else
                        {
                            MarkIfOnboard(oppAttackedBuf, s - 15);
                            MarkIfOnboard(oppAttackedBuf, s - 17);
                        }
                        break;

                    case Board.Knight:
                        foreach (var d in KnightDeltas) MarkIfOnboard(oppAttackedBuf, s + d);
                        break;

                    case Board.King:
                        foreach (var d in KingDeltas) MarkIfOnboard(oppAttackedBuf, s + d);
                        break;

                    case Board.Bishop:
                    case Board.Rook:
                    case Board.Queen:
                        int[] dirs = (ptype == Board.Bishop) ? BishopDeltas : (ptype == Board.Rook ? RookDeltas : QueenDeltas);
                        foreach (var d in dirs)
                        {
                            int t = s + d;
                            while ((t & 0x88) == 0)
                            {
                                oppAttackedBuf[t] = true;
                                sbyte occ = squares[t];
                                if (occ != Board.Empty) break;
                                t += d;
                            }
                        }
                        break;
                }
            }

            // 2) From king, scan each ray to detect pins and sliding checkers
            foreach (var d in QueenDeltas)
            {
                int s = myKingSq + d;
                int firstFriendly = -1;
                while ((s & 0x88) == 0)
                {
                    sbyte p = squares[s];
                    if (p != Board.Empty)
                    {
                        int color = (p > 6) ? Board.Black : Board.White;
                        int ptype = (p > 6) ? p - 6 : p;
                        if (color == myColor)
                        {
                            // first friendly piece could be pinned
                            if (firstFriendly == -1) firstFriendly = s;
                            else break; // blocked by second friendly piece -> cannot be pinned on this ray
                        }
                        else
                        {
                            // opponent piece encountered
                            bool isDiag = (d == 0x11 || d == 0x0f || d == -0x11 || d == -0x0f);
                            bool isOrth = (d == 0x10 || d == 0x01 || d == -0x10 || d == -0x01);
                            bool sliderMatches = (isDiag && (ptype == Board.Bishop || ptype == Board.Queen)) || (isOrth && (ptype == Board.Rook || ptype == Board.Queen));
                            if (sliderMatches)
                            {
                                if (firstFriendly == -1)
                                {
                                    // direct slider checking the king
                                    if (checkersCount < checkersArr.Length) checkersArr[checkersCount++] = s;
                                }
                                else
                                {
                                    // pinned friendly piece
                                    pinDirBuf[firstFriendly] = d;
                                }
                            }
                            // ray blocked regardless after hitting opponent piece
                            break;
                        }
                    }
                    s += d;
                }
            }

            // 3) Knight, pawn, and king checks (these won't create pins)
            // Pawn checkers
            if (oppColor == Board.White)
            {
                int p1 = myKingSq - 15; if ((p1 & 0x88) == 0 && squares[p1] == Board.Pawn && checkersCount < checkersArr.Length) checkersArr[checkersCount++] = p1;
                int p2 = myKingSq - 17; if ((p2 & 0x88) == 0 && squares[p2] == Board.Pawn && checkersCount < checkersArr.Length) checkersArr[checkersCount++] = p2;
            }
            else
            {
                int p1 = myKingSq + 15; if ((p1 & 0x88) == 0 && squares[p1] == (sbyte)(Board.Pawn + 6) && checkersCount < checkersArr.Length) checkersArr[checkersCount++] = p1;
                int p2 = myKingSq + 17; if ((p2 & 0x88) == 0 && squares[p2] == (sbyte)(Board.Pawn + 6) && checkersCount < checkersArr.Length) checkersArr[checkersCount++] = p2;
            }

            // Knight checkers
            foreach (var d in KnightDeltas)
            {
                int s = myKingSq + d;
                if ((s & 0x88) != 0) continue;
                sbyte q = squares[s];
                if (q == Board.Empty) continue;
                int color = (q > 6) ? Board.Black : Board.White;
                int ptype = (q > 6) ? q - 6 : q;
                if (color == oppColor && ptype == Board.Knight && checkersCount < checkersArr.Length) checkersArr[checkersCount++] = s;
            }

            // Opposing king adjacency (should be illegal positions normally but count as check)
            foreach (var d in KingDeltas)
            {
                int s = myKingSq + d;
                if ((s & 0x88) != 0) continue;
                sbyte q = squares[s];
                if (q == Board.Empty) continue;
                int color = (q > 6) ? Board.Black : Board.White;
                int ptype = (q > 6) ? q - 6 : q;
                if (color == oppColor && ptype == Board.King && checkersCount < checkersArr.Length) checkersArr[checkersCount++] = s;
            }
        }

        // Debug wrapper to expose the attack map/pins/checkers for external diagnostics
        public static void ComputeAttacksPinsForDebug(Board board, int myColor, out bool[] oppAttacked, out int[] pinDir, out List<int> checkers)
        {
            int myKingSq = (myColor == Board.White) ? board.WhiteKingSquare : board.BlackKingSquare;
            // fill shared buffers then return copies to caller
            ComputeOpponentAttacksPins(board, myColor, myKingSq, s_oppAttacked, s_pinDir, s_checkersArr, out s_checkersCount);
            oppAttacked = new bool[128]; Array.Copy(s_oppAttacked, oppAttacked, 128);
            pinDir = new int[128]; Array.Copy(s_pinDir, pinDir, 128);
            // copy checkers from the fixed-size array into a List<int> for debug caller
            checkers = new List<int>(s_checkersCount);
            for (int i = 0; i < s_checkersCount; i++) checkers.Add(s_checkersArr[i]);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void MarkIfOnboard(bool[] arr, int sq)
        {
            if ((sq & 0x88) == 0) arr[sq] = true;
        }

        // Return true if the move from->to stays along the pin-line defined by delta pd (+pd or -pd allowed)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool MoveIsOnPinLine(int from, int to, int pd)
        {
            if (pd == 0) return true;
            // forward along pd
            int s = from + pd;
            while ((s & 0x88) == 0)
            {
                if (s == to) return true;
                s += pd;
            }
            // backward along -pd
            s = from - pd;
            while ((s & 0x88) == 0)
            {
                if (s == to) return true;
                s -= pd;
            }
            return false;
        }

        // find direction delta (one of QueenDeltas) that connects a -> b (i.e., stepping by delta repeatedly reaches b)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int FindDirection(int a, int b)
        {
            foreach (var d in QueenDeltas)
            {
                int s = a + d;
                while ((s & 0x88) == 0)
                {
                    if (s == b) return d;
                    s += d;
                }
            }
            return 0;
        }

        // Generate king moves (no make/unmake) using oppAttacked map
        private static void GenerateKingLegalMoves(Board board, int kingSq, int stm, bool[] oppAttacked, List<Board.Move> moves)
        {
            GenerateKingLegalMovesForSquare(board, kingSq, stm, oppAttacked, moves);
        }

        private static void GenerateKingLegalMovesForSquare(Board board, int from, int stm, bool[] oppAttacked, List<Board.Move> moves)
        {
            int opp = stm == Board.White ? Board.Black : Board.White;
            var squares = board.Squares;

            foreach (var d in KingDeltas)
            {
                int to = from + d;
                if ((to & 0x88) != 0) continue;
                sbyte target = squares[to];
                if (target != Board.Empty)
                {
                    int col = (target > 6) ? Board.Black : Board.White;
                    if (col == stm) continue; // can't capture own piece
                }

                // Quick reject using precomputed attack map when possible
                if (oppAttacked[to]) continue;

                if (target == Board.Empty)
                {
                    // Cheap ray-test first; only simulate when the ray test indicates exposure
                    if (MoveExposesSlidingAttack(board, from, to, opp))
                    {
                        var mv = Board.Move.Create(from, to, 0, false, false, false, false);
                        board.MakeMoveWithUndo(mv, out var undo);
                        bool kingInCheck = IsSquareAttacked(board, to, opp);
                        UnmakeMove(board, mv, undo);
                        if (!kingInCheck) moves.Add(mv);
                    }
                    else moves.Add(Board.Move.Create(from, to, 0, false, false, false, false));
                }
                else
                {
                    // capture: always simulate to be safe
                    var mv = Board.Move.Create(from, to, 0, true, false, false, false);
                    board.MakeMoveWithUndo(mv, out var undo);
                    bool kingInCheck = IsSquareAttacked(board, to, opp);
                    UnmakeMove(board, mv, undo);
                    if (!kingInCheck) moves.Add(mv);
                }
            }
        }

        // Buffer variant of king move generation
        private static void GenerateKingLegalMovesBuffer(Board board, int from, int stm, bool[] oppAttacked, ref MoveBuffer moves)
        {
            int opp = stm == Board.White ? Board.Black : Board.White;
            var squares = board.Squares;

            foreach (var d in KingDeltas)
            {
                int to = from + d;
                if ((to & 0x88) != 0) continue;
                sbyte target = squares[to];
                if (target != Board.Empty)
                {
                    int col = (target > 6) ? Board.Black : Board.White;
                    if (col == stm) continue; // can't capture own piece
                }

                if (oppAttacked[to])
                {
                    continue;
                }

                // Always simulate king moves (capture or quiet) to be safe: cheap ray-test can miss some discovery cases
                var mvSim = Board.Move.Create(from, to, 0, target != Board.Empty, false, false, false);
                board.MakeMoveWithUndo(mvSim, out var undoSim);
                bool kingInCheckSim = IsSquareAttacked(board, (target == Board.Empty) ? to : to, opp);
                UnmakeMove(board, mvSim, undoSim);
                if (!kingInCheckSim) moves.Add(mvSim);
            }
        }

        // Return true if moving king from 'from' to 'to' would expose 'to' to a sliding attack from opponent
        private static bool MoveExposesSlidingAttack(Board board, int from, int to, int oppColor)
        {
            // For each queen direction, scan outwards from 'to'. If we encounter 'from' first and then
            // an opponent slider matching the direction before any other blocker, the move exposes an attack.
            var squares = board.Squares;
            foreach (var d in QueenDeltas)
            {
                int s = to + d;
                bool sawFrom = false;
                while ((s & 0x88) == 0)
                {
                    if (s == from) { sawFrom = true; s += d; continue; }
                    sbyte p = squares[s];
                    if (p != Board.Empty)
                    {
                        if (sawFrom)
                        {
                            int color = (p > 6) ? Board.Black : Board.White;
                            int ptype = (p > 6) ? p - 6 : p;
                            bool isDiag = (d == 0x11 || d == 0x0f || d == -0x11 || d == -0x0f);
                            bool isOrth = (d == 0x10 || d == 0x01 || d == -0x10 || d == -0x01);
                            bool sliderMatches = (isDiag && (ptype == Board.Bishop || ptype == Board.Queen)) || (isOrth && (ptype == Board.Rook || ptype == Board.Queen));
                            if (color == oppColor && sliderMatches) return true;
                        }
                        break;
                    }
                    s += d;
                }
            }
            return false;
        }

        // Buffer variants of pseudo generators
        // New API: generate pseudo pawn moves and call 'add' for each generated pseudo move
        private static void GeneratePawnMovesBuffer(Board board, int sq, int color, Action<Board.Move> add)
        {
            var squares = board.Squares;
            int dir = color == Board.White ? 0x10 : -0x10;
            int startRank = color == Board.White ? 1 : 6;
            int promoteRank = color == Board.White ? 6 : 1;
            int epSq = board.EnPassant;

            // Use occupancy bitboard for forward checks
            BuildPawnAndOccupancyBitboards(board, out var occ, out var whitePawns, out var blackPawns);
            int to = sq + dir;
            if ((to & 0x88) == 0)
            {
                int toIdx = SqToBitIndex(to);
                bool toEmpty = ((occ >> toIdx) & 1UL) == 0UL;
                if (toEmpty)
                {
                    if ((sq >> 4) == promoteRank)
                    {
                        for (int p = 2; p <= 5; p++) add(Board.Move.Create(sq, to, p, false, false, false, false));
                    }
                    else
                    {
                        add(Board.Move.Create(sq, to, 0, false, false, false, false));
                        int dbl = sq + dir * 2;
                        if ((sq >> 4) == startRank && (dbl & 0x88) == 0)
                        {
                            int dblIdx = SqToBitIndex(dbl);
                            bool dblEmpty = ((occ >> dblIdx) & 1UL) == 0UL;
                            if (dblEmpty) add(Board.Move.Create(sq, dbl, 0, false, false, false, true));
                        }
                    }
                }
            }

            for (int dx = -1; dx <= 1; dx += 2)
            {
                int cap = sq + dir + dx;
                if ((cap & 0x88) != 0) continue;
                sbyte target = squares[cap];
                if (target != Board.Empty)
                {
                    int tcolor = (target > 6) ? Board.Black : Board.White;
                    if (tcolor != color)
                    {
                        if ((sq >> 4) == promoteRank)
                        {
                            for (int p = 2; p <= 5; p++) add(Board.Move.Create(sq, cap, p, true, false, false, false));
                        }
                        else add(Board.Move.Create(sq, cap, 0, true, false, false, false));
                    }
                }

                if (cap == epSq)
                {
                    int epRankCheck = color == Board.White ? 4 : 3;
                    if ((sq >> 4) == epRankCheck && squares[sq] == (color == Board.White ? Board.Pawn : (sbyte)(Board.Pawn + 6)))
                        add(Board.Move.Create(sq, cap, 0, true, true, false, false));
                }
            }
        }

        private static void GenerateJumpMovesBuffer(Board board, int sq, int color, int[] deltas, bool king, Action<Board.Move> add)
        {
            var squares = board.Squares;
            foreach (var d in deltas)
            {
                int to = sq + d;
                if ((to & 0x88) != 0) continue;
                sbyte t = squares[to];
                if (t == Board.Empty) add(Board.Move.Create(sq, to, 0, false, false, false, false));
                else { int tcol = (t > 6) ? Board.Black : Board.White; if (tcol != color) add(Board.Move.Create(sq, to, 0, true, false, false, false)); }
            }
        }

        private static void GenerateSlideMovesBuffer(Board board, int sq, int color, int[] deltas, Action<Board.Move> add)
        {
            // Use sliding attack tables for rook/bishop/queen directions
            var squares = board.Squares;
            BuildPawnAndOccupancyBitboards(board, out var occ, out _, out _);
            int bitIndex = SqToBitIndex(sq);
            ulong attacks = 0UL;
            // determine which table to use by deltas (rook vs bishop)
            bool isRook = (deltas == RookDeltas);
            bool isBishop = (deltas == BishopDeltas);
            if (isRook) attacks = SlidingAttackTables.GetRookAttacks(bitIndex, occ);
            else if (isBishop) attacks = SlidingAttackTables.GetBishopAttacks(bitIndex, occ);
            else
            {
                // queen: union of rook and bishop
                attacks = SlidingAttackTables.GetRookAttacks(bitIndex, occ) | SlidingAttackTables.GetBishopAttacks(bitIndex, occ);
            }

            // Iterate over attack bits and translate to 0x88 squares
            while (attacks != 0UL)
            {
                int toBit = BitOperations.TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                int toSq = ((toBit >> 3) << 4) | (toBit & 7);
                sbyte occPiece = squares[toSq];
                if (occPiece == Board.Empty) add(Board.Move.Create(sq, toSq, 0, false, false, false, false));
                else { int tcol = (occPiece > 6) ? Board.Black : Board.White; if (tcol != color) add(Board.Move.Create(sq, toSq, 0, true, false, false, false)); }
            }
        }

        private static void GenerateLegalCastlingBuffer(Board board, int stm, bool[] oppAttacked, ref MoveBuffer moves)
        {
            int rank = stm == Board.White ? 0 : 7;
            int kingHome = (rank << 4) | 4;
            sbyte kingPiece = (stm == Board.White) ? Board.King : (sbyte)(Board.King + 6);
            if (board.Squares[kingHome] != kingPiece) return;
            int rights = board.CastlingRights;
            if (stm == Board.White)
            {
                if ((rights & 1) != 0)
                {
                    int f5 = (rank << 4) | 5; int f6 = (rank << 4) | 6; int rookSq = (rank << 4) | 7;
                    if (board.Squares[f5] == Board.Empty && board.Squares[f6] == Board.Empty && board.Squares[rookSq] == Board.Rook)
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f5] && !oppAttacked[f6]) moves.Add(Board.Move.Create(kingHome, f6, 0, false, false, true, false));
                    }
                }
                if ((rights & 2) != 0)
                {
                    int f3 = (rank << 4) | 3; int f2 = (rank << 4) | 2; int f1 = (rank << 4) | 1; int rookSq = (rank << 4) | 0;
                    if (board.Squares[f3] == Board.Empty && board.Squares[f2] == Board.Empty && board.Squares[f1] == Board.Empty && board.Squares[rookSq] == Board.Rook)
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f3] && !oppAttacked[f2]) moves.Add(Board.Move.Create(kingHome, f2, 0, false, false, true, false));
                    }
                }
            }
            else
            {
                if ((rights & 4) != 0)
                {
                    int f5 = (rank << 4) | 5; int f6 = (rank << 4) | 6; int rookSq = (rank << 4) | 7;
                    if (board.Squares[f5] == Board.Empty && board.Squares[f6] == Board.Empty && board.Squares[rookSq] == (sbyte)(Board.Rook + 6))
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f5] && !oppAttacked[f6]) moves.Add(Board.Move.Create(kingHome, f6, 0, false, false, true, false));
                    }
                }
                if ((rights & 8) != 0)
                {
                    int f3 = (rank << 4) | 3; int f2 = (rank << 4) | 2; int f1 = (rank << 4) | 1; int rookSq = (rank << 4) | 0;
                    if (board.Squares[f3] == Board.Empty && board.Squares[f2] == Board.Empty && board.Squares[f1] == Board.Empty && board.Squares[rookSq] == (sbyte)(Board.Rook + 6))
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f3] && !oppAttacked[f2]) moves.Add(Board.Move.Create(kingHome, f2, 0, false, false, true, false));
                    }
                }
            }
        }

        // Castling generation using oppAttacked map (rook checks require color-aware check)
        private static void GenerateLegalCastling(Board board, int stm, bool[] oppAttacked, List<Board.Move> moves)
        {
            int rank = stm == Board.White ? 0 : 7;
            int kingHome = (rank << 4) | 4;
            sbyte kingPiece = (stm == Board.White) ? Board.King : (sbyte)(Board.King + 6);

            if (board.Squares[kingHome] != kingPiece) return;

            int rights = board.CastlingRights;
            if (stm == Board.White)
            {
                // White kingside K
                if ((rights & 1) != 0)
                {
                    int f5 = (rank << 4) | 5;
                    int f6 = (rank << 4) | 6;
                    int rookSq = (rank << 4) | 7;
                    if (board.Squares[f5] == Board.Empty && board.Squares[f6] == Board.Empty &&
                        board.Squares[rookSq] == Board.Rook)
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f5] && !oppAttacked[f6])
                            moves.Add(Board.Move.Create(kingHome, f6, 0, false, false, true, false));
                    }
                }
                // White queenside Q
                if ((rights & 2) != 0)
                {
                    int f3 = (rank << 4) | 3;
                    int f2 = (rank << 4) | 2;
                    int f1 = (rank << 4) | 1;
                    int rookSq = (rank << 4) | 0;
                    if (board.Squares[f3] == Board.Empty && board.Squares[f2] == Board.Empty && board.Squares[f1] == Board.Empty &&
                        board.Squares[rookSq] == Board.Rook)
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f3] && !oppAttacked[f2])
                            moves.Add(Board.Move.Create(kingHome, f2, 0, false, false, true, false));
                    }
                }
            }
            else
            {
                // Black kingside k
                if ((rights & 4) != 0)
                {
                    int f5 = (rank << 4) | 5;
                    int f6 = (rank << 4) | 6;
                    int rookSq = (rank << 4) | 7;
                    if (board.Squares[f5] == Board.Empty && board.Squares[f6] == Board.Empty &&
                        board.Squares[rookSq] == (sbyte)(Board.Rook + 6))
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f5] && !oppAttacked[f6])
                            moves.Add(Board.Move.Create(kingHome, f6, 0, false, false, true, false));
                    }
                }
                // Black queenside q
                if ((rights & 8) != 0)
                {
                    int f3 = (rank << 4) | 3;
                    int f2 = (rank << 4) | 2;
                    int f1 = (rank << 4) | 1;
                    int rookSq = (rank << 4) | 0;
                    if (board.Squares[f3] == Board.Empty && board.Squares[f2] == Board.Empty && board.Squares[f1] == Board.Empty &&
                        board.Squares[rookSq] == (sbyte)(Board.Rook + 6))
                    {
                        if (!oppAttacked[kingHome] && !oppAttacked[f3] && !oppAttacked[f2])
                            moves.Add(Board.Move.Create(kingHome, f2, 0, false, false, true, false));
                    }
                }
            }
        }

        // Check if a square is attacked by a given color (fallback / validation)
        private static bool IsSquareAttacked(Board board, int sq, int attackerColor)
        {
            if ((sq & 0x88) != 0) return false;
            var squares = board.Squares;

            // Pawn attacks:
            if (attackerColor == Board.White)
            {
                int p1 = sq - 15; if ((p1 & 0x88) == 0 && squares[p1] == Board.Pawn) return true;
                int p2 = sq - 17; if ((p2 & 0x88) == 0 && squares[p2] == Board.Pawn) return true;
            }
            else
            {
                int p1 = sq + 15; if ((p1 & 0x88) == 0 && squares[p1] == (sbyte)(Board.Pawn + 6)) return true;
                int p2 = sq + 17; if ((p2 & 0x88) == 0 && squares[p2] == (sbyte)(Board.Pawn + 6)) return true;
            }

            // Knights
            foreach (var d in KnightDeltas)
            {
                int s = sq + d;
                if ((s & 0x88) != 0) continue;
                sbyte pc = squares[s];
                if (pc == Board.Empty) continue;
                int color = (pc > 6) ? Board.Black : Board.White;
                int ptype = (pc > 6) ? pc - 6 : pc;
                if (color == attackerColor && ptype == Board.Knight) return true;
            }

            // King adjacency
            foreach (var d in KingDeltas)
            {
                int s = sq + d;
                if ((s & 0x88) != 0) continue;
                sbyte pc = squares[s];
                if (pc == Board.Empty) continue;
                int color = (pc > 6) ? Board.Black : Board.White;
                int ptype = (pc > 6) ? pc - 6 : pc;
                if (color == attackerColor && ptype == Board.King) return true;
            }

            // Sliding rook-like
            foreach (var d in RookDeltas)
            {
                int s = sq + d;
                while ((s & 0x88) == 0)
                {
                    sbyte pc = squares[s];
                    if (pc != Board.Empty)
                    {
                        int color = (pc > 6) ? Board.Black : Board.White;
                        int ptype = (pc > 6) ? pc - 6 : pc;
                        if (color == attackerColor && (ptype == Board.Rook || ptype == Board.Queen)) return true;
                        break;
                    }
                    s += d;
                }
            }

            // Sliding bishop-like
            foreach (var d in BishopDeltas)
            {
                int s = sq + d;
                while ((s & 0x88) == 0)
                {
                    sbyte pc = squares[s];
                    if (pc != Board.Empty)
                    {
                        int color = (pc > 6) ? Board.Black : Board.White;
                        int ptype = (pc > 6) ? pc - 6 : pc;
                        if (color == attackerColor && (ptype == Board.Bishop || ptype == Board.Queen)) return true;
                        break;
                    }
                    s += d;
                }
            }

            return false;
        }

        // ---------------- Pseudo move generators re-used ----------------

        private static void GeneratePawnMoves(Board board, int sq, int color, List<Board.Move> moves)
        {
            var squares = board.Squares;
            int dir = color == Board.White ? 0x10 : -0x10;
            int startRank = color == Board.White ? 1 : 6;
            int promoteRank = color == Board.White ? 6 : 1;
            int epSq = board.EnPassant;

            BuildPawnAndOccupancyBitboards(board, out var occ, out var whitePawns, out var blackPawns);

            int to = sq + dir;
            if ((to & 0x88) == 0)
            {
                int toIdx = SqToBitIndex(to);
                bool toEmpty = ((occ >> toIdx) & 1UL) == 0UL;
                if (toEmpty)
                {
                    if ((sq >> 4) == promoteRank)
                    {
                        for (int p = 2; p <= 5; p++)
                            moves.Add(Board.Move.Create(sq, to, p, false, false, false, false));
                    }
                    else
                    {
                        moves.Add(Board.Move.Create(sq, to, 0, false, false, false, false));
                        int dbl = sq + dir * 2;
                        if ((sq >> 4) == startRank && (dbl & 0x88) == 0)
                        {
                            int dblIdx = SqToBitIndex(dbl);
                            bool dblEmpty = ((occ >> dblIdx) & 1UL) == 0UL;
                            if (dblEmpty) moves.Add(Board.Move.Create(sq, dbl, 0, false, false, false, true));
                        }
                    }
                }
            }

            // captures (including en-passant)
            for (int dx = -1; dx <= 1; dx += 2)
            {
                int cap = sq + dir + dx;
                if ((cap & 0x88) != 0) continue;
                sbyte target = squares[cap];
                if (target != Board.Empty)
                {
                    int tcolor = (target > 6) ? Board.Black : Board.White;
                    if (tcolor != color)
                    {
                        if ((sq >> 4) == promoteRank)
                        {
                            for (int p = 2; p <= 5; p++)
                                moves.Add(Board.Move.Create(sq, cap, p, true, false, false, false));
                        }
                        else
                            moves.Add(Board.Move.Create(sq, cap, 0, true, false, false, false));
                    }
                }

                // en-passant target square match
                if (cap == epSq)
                {
                    int epRankCheck = color == Board.White ? 4 : 3;
                    if ((sq >> 4) == epRankCheck && squares[sq] == (color == Board.White ? Board.Pawn : (sbyte)(Board.Pawn + 6)))
                        moves.Add(Board.Move.Create(sq, cap, 0, true, true, false, false));
                }
            }
        }

        private static void GenerateJumpMoves(Board board, int sq, int color, int[] deltas, bool king, List<Board.Move> moves)
        {
            foreach (var d in deltas)
            {
                int to = sq + d;
                if ((to & 0x88) != 0) continue;
                var squares = board.Squares;
                sbyte t = squares[to];
                if (t == Board.Empty)
                    moves.Add(Board.Move.Create(sq, to, 0, false, false, false, false));
                else
                {
                    int tcol = (t > 6) ? Board.Black : Board.White;
                    if (tcol != color) moves.Add(Board.Move.Create(sq, to, 0, true, false, false, false));
                }
            }
        }

        private static void GenerateSlideMoves(Board board, int sq, int color, int[] deltas, List<Board.Move> moves)
        {
            var squares = board.Squares;
            BuildPawnAndOccupancyBitboards(board, out var occ, out _, out _);
            int bitIndex = SqToBitIndex(sq);
            ulong attacks = 0UL;
            bool isRook = (deltas == RookDeltas);
            bool isBishop = (deltas == BishopDeltas);
            if (isRook) attacks = SlidingAttackTables.GetRookAttacks(bitIndex, occ);
            else if (isBishop) attacks = SlidingAttackTables.GetBishopAttacks(bitIndex, occ);
            else attacks = SlidingAttackTables.GetRookAttacks(bitIndex, occ) | SlidingAttackTables.GetBishopAttacks(bitIndex, occ);

            while (attacks != 0UL)
            {
                int toBit = BitOperations.TrailingZeroCount(attacks);
                attacks &= attacks - 1;
                int toSq = ((toBit >> 3) << 4) | (toBit & 7);
                sbyte occPiece = squares[toSq];
                if (occPiece == Board.Empty) moves.Add(Board.Move.Create(sq, toSq, 0, false, false, false, false));
                else { int tcol = (occPiece > 6) ? Board.Black : Board.White; if (tcol != color) moves.Add(Board.Move.Create(sq, toSq, 0, true, false, false, false)); }
            }
        }

        // Helper to unmake a move using undo struct (keeps this local copy; board also has its own)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void UnmakeMove(Board board, Board.Move move, Board.Undo undo)
        {
            int from = Board.Move.GetFrom(move);
            int to = Board.Move.GetTo(move);
            int promo = Board.Move.GetPromotion(move);
            int mv = (int)move;
            bool isPromotion = promo != 0;
            bool isEnPassant = ((mv >> 18) & 1) != 0;
            bool isCastle = ((mv >> 19) & 1) != 0;

            var squares = board.Squares;
            // Fast-path: if move was the common quiet fast-path, undo minimal state changes
            if (undo.wasQuiet)
            {
                // move piece back and clear 'to'
                squares[from] = squares[to];
                squares[to] = Board.Empty;

                // restore king squares quickly if necessary
                if (squares[from] == Board.King) board.WhiteKingSquare = undo.oldWhiteKingSquare;
                else if (squares[from] == (Board.King + 6)) board.BlackKingSquare = undo.oldBlackKingSquare;

                // restore state
                board.EnPassant = undo.oldEnPassant;
                board.CastlingRights = undo.oldCastlingRights;
                board.HalfmoveClock = undo.oldHalfmoveClock;
                board.FullmoveNumber = undo.oldFullmoveNumber;
                board.SideToMove = undo.oldSideToMove;
                return;
            }

            sbyte movedPiece = squares[to];

            if (isPromotion)
            {
                // Replace promoted piece with pawn and restore captured piece
                sbyte pawn = (movedPiece > 6) ? (sbyte)(Board.Pawn + 6) : Board.Pawn;
                squares[from] = pawn;
                squares[to] = undo.capturedPiece;
            }
            else if (isEnPassant)
            {
                // capturing pawn is on 'to' — move it back to 'from' and restore captured pawn behind 'to'
                squares[from] = squares[to];
                squares[to] = Board.Empty;
                int capSq = (undo.oldSideToMove == Board.White) ? (to - 0x10) : (to + 0x10);
                squares[capSq] = undo.capturedPiece;
            }
            else if (isCastle)
            {
                // restore king and rook
                squares[from] = squares[to];
                squares[to] = Board.Empty;
                int rank = from >> 4;
                if (to == ((rank << 4) | 6))
                {
                    // kingside: rook f -> h
                    squares[(rank << 4) | 7] = squares[(rank << 4) | 5];
                    squares[(rank << 4) | 5] = Board.Empty;
                }
                else if (to == ((rank << 4) | 2))
                {
                    // queenside: rook d -> a
                    squares[(rank << 4) | 0] = squares[(rank << 4) | 3];
                    squares[(rank << 4) | 3] = Board.Empty;
                }
            }
            else
            {
                // normal move or capture
                squares[from] = squares[to];
                squares[to] = undo.capturedPiece;
            }

            // restore state
            board.EnPassant = undo.oldEnPassant;
            board.CastlingRights = undo.oldCastlingRights;
            board.HalfmoveClock = undo.oldHalfmoveClock;
            board.FullmoveNumber = undo.oldFullmoveNumber;
            board.SideToMove = undo.oldSideToMove;
            board.WhiteKingSquare = undo.oldWhiteKingSquare;
            board.BlackKingSquare = undo.oldBlackKingSquare;
        }

        // Perft helpers (unchanged logic)
        public static long Perft(Board board, int depth)
        {
            if (depth == 0) return 1;
            // use a small pool of MoveBuffers indexed by depth to avoid allocating a new list for every node
            var pool = new MoveBuffer[depth + 1];
            for (int i = 0; i <= depth; i++) pool[i] = new MoveBuffer(128);
            return PerftFast(board, depth, pool);
        }

        // Internal perft that reuses the provided pool of MoveBuffers to avoid allocations
        private static long PerftFast(Board board, int depth, MoveBuffer[] pool)
        {
            if (depth == 0) return 1;
            var moves = pool[depth];
            moves.Clear();
            GenerateMovesBuffer(board, ref moves);

            if (DiagnosticsEnabled)
            {
                var brute = GenerateMovesBruteforce(board);
                if (brute.Count != moves.Count)
                {
                    Console.Error.WriteLine($"[Promethius] MOVEGEN MISMATCH at depth {depth}: fast={moves.Count} brute={brute.Count} FEN={board.ToFEN()}");
                    var bruteSet = new HashSet<int>(brute.ConvertAll(m => (int)m));
                    for (int i = 0; i < moves.Count; i++) if (!bruteSet.Contains((int)moves[i])) Console.Error.WriteLine($"[Promethius] extra fast: {MoveToUci(moves[i])}");
                    var fastSet = new HashSet<int>();
                    for (int i = 0; i < moves.Count; i++) fastSet.Add((int)moves[i]);
                    foreach (var mv in brute) if (!fastSet.Contains((int)mv)) Console.Error.WriteLine($"[Promethius] missing fast: {MoveToUci(mv)}");
                    throw new Exception("Movegen mismatch detected - see stderr");
                }
            }

            long nodes = 0;
            for (int i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                board.MakeMoveWithUndo(move, out var undo);
                nodes += PerftFast(board, depth - 1, pool);
                UnmakeMove(board, move, undo);
            }
            return nodes;
        }

        public static List<(Board.Move, long)> PerftDivide(Board board, int depth)
        {
            var ret = new List<(Board.Move, long)>();
            Span<Board.Move> movesSpan = stackalloc Board.Move[218];
            int movesCount;
            GenerateMovesSpan(board, movesSpan, out movesCount);
            for (int i = 0; i < movesCount; i++)
            {
                var mv = movesSpan[i];
                board.MakeMoveWithUndo(mv, out var undo);
                long nodes = Perft(board, depth - 1);
                UnmakeMove(board, mv, undo);
                ret.Add((mv, nodes));
            }
            return ret;
        }

        // Debug helper: generate legal moves by brute-force making/unmaking each pseudo-legal move.
        // This is slower but useful to validate the fast legality filters.
        public static List<Board.Move> GenerateMovesBruteforce(Board board)
        {
            var legal = new List<Board.Move>(64);
            int stm = board.SideToMove;
            int opp = stm == Board.White ? Board.Black : Board.White;

            // generate pseudo moves similar to GenerateMoves but validate by making the move
            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;
                sbyte piece = board.Squares[sq];
                if (piece == Board.Empty) continue;
                int color = (piece > 6) ? Board.Black : Board.White;
                if (color != stm) continue;

                int ptype = (piece > 6) ? piece - 6 : piece;

                if (ptype == Board.King)
                {
                    // king moves
                    foreach (var d in KingDeltas)
                    {
                        int to = sq + d;
                        if ((to & 0x88) != 0) continue;
                        sbyte target = board.Squares[to];
                        if (target != Board.Empty)
                        {
                            int col = (target > 6) ? Board.Black : Board.White;
                            if (col == stm) continue;
                        }
                        var mv = Board.Move.Create(sq, to, 0, board.Squares[to] != Board.Empty, false, false, false);
                        board.MakeMoveWithUndo(mv, out var undo);
                        int kingSq = stm == Board.White ? board.WhiteKingSquare : board.BlackKingSquare;
                        bool kingInCheck = IsSquareAttacked(board, kingSq, opp);
                        UnmakeMove(board, mv, undo);
                        if (!kingInCheck) legal.Add(mv);
                    }
                    continue;
                }

                // build pseudo moves using a stackalloc buffer to avoid List allocations
                // Use MoveBuffer to avoid List allocations for pseudo move generation
                var pseudoBuf = new MoveBuffer(32);
                pseudoBuf.Clear();
                int pseudoCount = 0;
                void AddPseudo(Board.Move m) { pseudoBuf.Add(m); pseudoCount = pseudoBuf.Count; }

                switch (ptype)
                {
                    case Board.Pawn:
                        GeneratePawnMovesBuffer(board, sq, color, m => AddPseudo(m));
                        break;
                    case Board.Knight:
                        GenerateJumpMovesBuffer(board, sq, color, KnightDeltas, false, m => AddPseudo(m));
                        break;
                    case Board.Bishop:
                        GenerateSlideMovesBuffer(board, sq, color, BishopDeltas, m => AddPseudo(m));
                        break;
                    case Board.Rook:
                        GenerateSlideMovesBuffer(board, sq, color, RookDeltas, m => AddPseudo(m));
                        break;
                    case Board.Queen:
                        GenerateSlideMovesBuffer(board, sq, color, QueenDeltas, m => AddPseudo(m));
                        break;
                }

                for (int pi = 0; pi < pseudoCount; pi++)
                {
                    var mv = pseudoBuf[pi];
                    board.MakeMoveWithUndo(mv, out var undo);
                    int kingSq = stm == Board.White ? board.WhiteKingSquare : board.BlackKingSquare;
                    bool kingInCheck = IsSquareAttacked(board, kingSq, opp);
                    UnmakeMove(board, mv, undo);
                    if (!kingInCheck) legal.Add(mv);
                }
            }

            // castling: compute same oppAttacked map as the fast generator and reuse its castling checks
            var castleCandidates = new List<Board.Move>(4);
            // use shared buffers and copy results for the brute-force path
            int myKingSq = (stm == Board.White) ? board.WhiteKingSquare : board.BlackKingSquare;
            ComputeOpponentAttacksPins(board, stm, myKingSq, s_oppAttacked, s_pinDir, s_checkersArr, out s_checkersCount);
            List<int> checkers = new List<int>(s_checkersCount);
            for (int i = 0; i < s_checkersCount; i++) checkers.Add(s_checkersArr[i]);
            // GenerateLegalCastling only reads oppAttacked; we can pass the shared buffer directly
            GenerateLegalCastling(board, stm, s_oppAttacked, castleCandidates);
            foreach (var mv in castleCandidates)
            {
                // validate by making the move as well (final sanity)
                board.MakeMoveWithUndo(mv, out var undo);
                int kingSq = stm == Board.White ? board.WhiteKingSquare : board.BlackKingSquare;
                bool kingInCheck = IsSquareAttacked(board, kingSq, opp);
                UnmakeMove(board, mv, undo);
                if (!kingInCheck) legal.Add(mv);
            }

            return legal;
        }

        // ----------------------- Public helper utilities -----------------------

        // Return only legal capture moves for the current position
        public static List<Board.Move> GenerateCaptures(Board board)
        {
            var captures = new List<Board.Move>();
            Span<Board.Move> span = stackalloc Board.Move[218];
            GenerateMovesSpan(board, span, out int count);
            for (int i = 0; i < count; i++)
            {
                int mv = (int)span[i];
                if (((mv >> 17) & 1) != 0) captures.Add(span[i]);
            }
            return captures;
        }

        // Is the side to move currently in check?
        public static bool IsInCheck(Board board)
        {
            int stm = board.SideToMove;
            int kingSq = (stm == Board.White) ? board.WhiteKingSquare : board.BlackKingSquare;
            int opp = stm == Board.White ? Board.Black : Board.White;
            return IsSquareAttacked(board, kingSq, opp);
        }

        // Quick helper: are there any legal moves available?
        public static bool HasLegalMoves(Board board)
        {
            Span<Board.Move> span = stackalloc Board.Move[218];
            GenerateMovesSpan(board, span, out int count);
            return count > 0;
        }

        public static bool IsInCheckmate(Board board)
        {
            return IsInCheck(board) && !HasLegalMoves(board);
        }

        public static bool IsInStalemate(Board board)
        {
            return !IsInCheck(board) && !HasLegalMoves(board);
        }

        // Fifty-move rule: 50 full moves = 100 half-moves
        public static bool IsFiftyMoveDraw(Board board)
        {
            return board.HalfmoveClock >= 100;
        }

        // Basic insufficient material check (covers most common draw cases):
        // - King vs King
        // - King vs King + (single) minor piece (B or N)
        // - King + bishop vs King + bishop where both bishops are on same color
        // This is conservative and intended as a fast heuristic.
        public static bool IsInsufficientMaterial(Board board)
        {
            var squares = board.Squares;
            int pawnCount = 0;
            int majorCount = 0; // rooks/queens
            int minorCount = 0; // bishops/knights
            var bishopSquares = new System.Collections.Generic.List<int>();

            for (int s = 0; s < 128; s++)
            {
                if ((s & 0x88) != 0) continue;
                sbyte p = squares[s];
                if (p == Board.Empty) continue;
                int ptype = (p > 6) ? p - 6 : p;
                switch (ptype)
                {
                    case Board.Pawn: pawnCount++; break;
                    case Board.Rook:
                    case Board.Queen: majorCount++; break;
                    case Board.Bishop: minorCount++; bishopSquares.Add(s); break;
                    case Board.Knight: minorCount++; break;
                }
            }

            if (pawnCount > 0 || majorCount > 0) return false;

            if (minorCount == 0) return true; // K vs K
            if (minorCount == 1) return true; // single minor piece
            if (minorCount == 2 && bishopSquares.Count == 2)
            {
                // if both bishops are on same color, mate is impossible
                int c0 = ((bishopSquares[0] & 0xF) + (bishopSquares[0] >> 4)) & 1;
                int c1 = ((bishopSquares[1] & 0xF) + (bishopSquares[1] >> 4)) & 1;
                if (c0 == c1) return true;
            }

            return false;
        }

        // Repetition detection: caller must provide a list of historic Zobrist keys (older to newer).
        // This function counts occurrences of the current board key in the provided history and
        // returns true if occurrences (including the current position) >= neededOccurrences.
        public static bool IsRepeatedPosition(Board board, System.Collections.Generic.IReadOnlyList<ulong> history, int neededOccurrences = 3)
        {
            if (history == null) return false;
            ulong key = board.ZobristKey;
            int count = 0;
            foreach (var h in history) if (h == key) count++;
            // include the current position as an occurrence
            return (count + 1) >= neededOccurrences;
        }

        // Aggregate draw test: checks stalemate, fifty-move, insufficient material and repetition (if history provided)
        public static bool IsDraw(Board board, System.Collections.Generic.IReadOnlyList<ulong>? history = null)
        {
            if (IsInsufficientMaterial(board)) return true;
            if (IsFiftyMoveDraw(board)) return true;
            if (history != null && IsRepeatedPosition(board, history)) return true;
            if (IsInStalemate(board)) return true;
            return false;
        }
    }
}

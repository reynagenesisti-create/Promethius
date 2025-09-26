using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PromethiusEngine
{
    public static class OpeningBook
    {
        // map ZobristKey -> list of candidate moves (first ~10 plies)
        private static Dictionary<ulong, List<Board.Move>> s_book = new Dictionary<ulong, List<Board.Move>>();
        private static Random s_rng = new Random();

        // How many plies from the game start to include (10 plies ~= first 5 moves each side)
        private const int MaxPliesToStore = 10;

        /// <summary>
        /// Load games file. Each line should be SAN moves separated by spaces (one game per line).
        /// Logs: info string opening book loaded: {positions} positions from ~{games} games
        /// </summary>
        public static void Load(string path)
        {
            s_book.Clear();

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"info string opening book file not found: {path}");
                return;
            }

            int loadedGames = 0;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine?.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // tokens: space-separated SAN moves
                var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var board = new Board();
                board.LoadFEN(Board.StartFEN);

                int ply = 0;
                for (int i = 0; i < tokens.Length && ply < MaxPliesToStore; i++)
                {
                    string san = tokens[i].Trim();
                    if (string.IsNullOrEmpty(san)) continue;

                    // compute key BEFORE making this move
                    ulong key = board.ZobristKey;

                    if (TryParseSanMove(board, san, out Board.Move mv))
                    {
                        if (!s_book.TryGetValue(key, out var list))
                        {
                            list = new List<Board.Move>();
                            s_book[key] = list;
                        }
                        list.Add(mv);

                        // apply the move and continue (use engine's MakeMoveWithUndo)
                        board.MakeMoveWithUndo(mv, out var undo);
                        // no need to unmake here because we created a fresh board for this line
                        ply++;
                    }
                    else
                    {
                        // if SAN parsing failed, abandon the remainder of this line (safety)
                        break;
                    }
                }

                loadedGames++;
            }

            Console.Error.WriteLine($"info string opening book loaded: {s_book.Count} positions from ~{loadedGames} games");
        }

        /// <summary>
        /// If current board position exists in the book, returns a random legal candidate move (from the book).
        /// Ensures returned move is legal in the current position (sanity).
        /// </summary>
        public static bool TryGetRandomMove(Board board, out Board.Move mvOut)
        {
            mvOut = (Board.Move)0;
            ulong key = board.ZobristKey;
            if (!s_book.TryGetValue(key, out var candidates) || candidates.Count == 0) return false;

            // Build a set of legal moves (quick check by integer representation) using stackalloc
            Span<Board.Move> legalSpan = stackalloc Board.Move[218];
            int legalCount;
            MoveGenerator.GenerateMovesSpan(board, legalSpan, out legalCount);
            var legalSet = new HashSet<int>(legalCount);
            for (int i = 0; i < legalCount; i++) legalSet.Add((int)legalSpan[i]);

            // Shuffle candidates and return first that is legal
            var shuffled = candidates.OrderBy(_ => s_rng.Next()).ToList();
            foreach (var cand in shuffled)
            {
                if (legalSet.Contains((int)cand))
                {
                    mvOut = cand;
                    return true;
                }
            }

            // no candidate matched a legal move -> fail
            return false;
        }

        // ---------------------- SAN parsing (best-effort) ----------------------
        // Generate all legal moves at the position and try to match SAN to one.
        private static bool TryParseSanMove(Board board, string sanRaw, out Board.Move mvOut)
        {
            mvOut = (Board.Move)0;
            if (string.IsNullOrWhiteSpace(sanRaw)) return false;

            string san = sanRaw.Trim();

            // strip check/mate markers
            san = san.TrimEnd('+', '#');

            // normalize × to x
            san = san.Replace('×', 'x');

            // Castling (O-O, O-O-O, 0-0, 0-0-0)
            if (san == "O-O" || san == "0-0" || san == "O-O-O" || san == "0-0-0")
            {
                int kingFrom = (board.SideToMove == Board.White) ? board.WhiteKingSquare : board.BlackKingSquare;
                int kingTo;
                bool kingside = san.StartsWith("O-O") || san.StartsWith("0-0");
                if (kingside)
                    kingTo = (board.SideToMove == Board.White) ? ((0 << 4) | 6) : ((7 << 4) | 6);
                else
                    kingTo = (board.SideToMove == Board.White) ? ((0 << 4) | 2) : ((7 << 4) | 2);

                Span<Board.Move> legal = stackalloc Board.Move[218];
                int legalCount2;
                MoveGenerator.GenerateMovesSpan(board, legal, out legalCount2);
                for (int i = 0; i < legalCount2; i++)
                {
                    var m = legal[i];
                    if (Board.Move.GetFrom(m) == kingFrom && Board.Move.GetTo(m) == kingTo)
                    {
                        mvOut = m;
                        return true;
                    }
                }
                return false;
            }

            // promotion detection (e8=Q or e8q)
            int? promotion = null;
            if (san.Contains('='))
            {
                int idx = san.IndexOf('=');
                if (idx + 1 < san.Length)
                {
                    promotion = CharToPromoCode(san[idx + 1], board.SideToMove);
                }
                san = san.Substring(0, idx);
            }
            else if (san.Length >= 3)
            {
                char last = san[san.Length - 1];
                if ("QRBNqrbn".IndexOf(last) >= 0 && san.Length >= 3 && char.IsDigit(san[san.Length - 2]))
                {
                    promotion = CharToPromoCode(last, board.SideToMove);
                    san = san.Substring(0, san.Length - 1);
                }
            }

            // find destination square (last file+rank occurrence)
            int destSquare = -1;
            for (int i = san.Length - 2; i >= 0; i--)
            {
                if (i >= 0 && i + 1 < san.Length)
                {
                    char f = san[i];
                    char r = san[i + 1];
                    if (f >= 'a' && f <= 'h' && r >= '1' && r <= '8')
                    {
                        destSquare = AlgebToSquareIndex(san.Substring(i, 2));
                        san = san.Substring(0, i); // chop off dest for remaining parsing
                        break;
                    }
                }
            }
            if (destSquare < 0) return false;

            // detect piece letter (KQRBN) otherwise pawn move
            bool isPieceMove = false;
            char pieceChar = '\0';
            if (san.Length > 0 && "KQRBN".IndexOf(san[0]) >= 0)
            {
                isPieceMove = true;
                pieceChar = san[0];
                san = san.Substring(1);
            }

            // remove capture marker
            san = san.Replace("x", "");

            // disambiguation: may include file or rank or both
            char? disFile = null;
            char? disRank = null;
            if (san.Length >= 1)
            {
                foreach (char c in san)
                {
                    if (c >= 'a' && c <= 'h') disFile = c;
                    if (c >= '1' && c <= '8') disRank = c;
                }
            }

            // gather legal moves and filter (use stackalloc span to avoid allocations)
            Span<Board.Move> legalMoves = stackalloc Board.Move[218];
            int legalCount3;
            MoveGenerator.GenerateMovesSpan(board, legalMoves, out legalCount3);

            var candidates = new List<Board.Move>();
            for (int i = 0; i < legalCount3; i++)
            {
                var m = legalMoves[i];
                if (Board.Move.GetTo(m) != destSquare) continue;

                int from = Board.Move.GetFrom(m);
                sbyte moving = board.Squares[from];

                // piece match
                if (isPieceMove)
                {
                    sbyte expected = PieceCharToBoardPiece(pieceChar, board.SideToMove);
                    if (moving != expected) continue;
                }
                else
                {
                    // pawn move
                    sbyte pawn = (board.SideToMove == Board.White) ? (sbyte)Board.Pawn : (sbyte)(Board.Pawn + 6);
                    if (moving != pawn) continue;
                }

                // promotion match
                int promoOfMove = Board.Move.GetPromotion(m);
                if (promotion.HasValue)
                {
                    if (promoOfMove != promotion.Value) continue;
                }
                else
                {
                    if (promoOfMove != 0) continue;
                }

                // disambiguation checks
                if (disFile.HasValue)
                {
                    int fromFile = from & 0xF;
                    if (fromFile != (disFile.Value - 'a')) continue;
                }
                if (disRank.HasValue)
                {
                    int fromRank = from >> 4;
                    if (fromRank != (disRank.Value - '1')) continue;
                }

                candidates.Add(m);
            }

            if (candidates.Count >= 1)
            {
                // if ambiguous, just pick the first — it's fine for book-building
                mvOut = candidates[0];
                return true;
            }

            return false;
        }

        // helper: convert algebraic like "e4" to 0x88 index (rank<<4 | file)
        private static int AlgebToSquareIndex(string sq)
        {
            if (sq == null || sq.Length != 2) throw new ArgumentException(nameof(sq));
            int file = sq[0] - 'a';
            int rank = sq[1] - '1';
            return (rank << 4) | file;
        }

        // helper: map piece char to board-encoded piece
        private static sbyte PieceCharToBoardPiece(char c, int sideToMove)
        {
            bool white = sideToMove == Board.White;
            return c switch
            {
                'K' => (sbyte)(white ? Board.King : (Board.King + 6)),
                'Q' => (sbyte)(white ? Board.Queen : (Board.Queen + 6)),
                'R' => (sbyte)(white ? Board.Rook : (Board.Rook + 6)),
                'B' => (sbyte)(white ? Board.Bishop : (Board.Bishop + 6)),
                'N' => (sbyte)(white ? Board.Knight : (Board.Knight + 6)),
                _ => (sbyte)(white ? Board.Pawn : (Board.Pawn + 6)),
            };
        }

        // map promotion char to promotion code used by Board.Move (2=n,3=b,4=r,5=q)
        private static int CharToPromoCode(char c, int sideToMove)
        {
            char lc = char.ToLowerInvariant(c);
            return lc switch
            {
                'n' => 2,
                'b' => 3,
                'r' => 4,
                'q' => 5,
                _ => 5,
            };
        }
    }
}

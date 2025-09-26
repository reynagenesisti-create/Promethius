using System;
using System.Text;

namespace PromethiusEngine
{
    public class Board
    {
        // Piece constants
        public const sbyte Empty = 0;
        public const sbyte Pawn = 1;
        public const sbyte Knight = 2;
        public const sbyte Bishop = 3;
        public const sbyte Rook = 4;
        public const sbyte Queen = 5;
        public const sbyte King = 6;

        // Color constants
        public const int White = 0;
        public const int Black = 1;

        // 0x88 board representation (128 squares)
        private sbyte[] squares = new sbyte[128];
        public sbyte[] Squares => squares;

        // Board state
        public int SideToMove { get; internal set; } = White;
        public int CastlingRights { get; internal set; } = 0; // 4 bits: KQkq
        public int EnPassant { get; internal set; } = -1;
        public int HalfmoveClock { get; internal set; } = 0;
        public int FullmoveNumber { get; internal set; } = 1;

        public int WhiteKingSquare = (0 << 4) | 4; // e1
        public int BlackKingSquare = (7 << 4) | 4; // e8

        // Zobrist hash
        public ulong ZobristKey;

        public static readonly string StartFEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        // Zobrist tables
        private static readonly ulong[,] PieceZobrist = new ulong[12, 128];
        private static readonly ulong[] CastlingZobrist = new ulong[16];
        private static readonly ulong[] EpZobrist = new ulong[128];
        private static readonly ulong SideToMoveZobrist;

        static Board()
        {
            var rng = new Random(0xC0FFEE);
            byte[] buf = new byte[8];
            for (int p = 0; p < 12; p++)
            {
                for (int s = 0; s < 128; s++)
                {
                    rng.NextBytes(buf);
                    PieceZobrist[p, s] = BitConverter.ToUInt64(buf, 0);
                }
            }
            for (int i = 0; i < CastlingZobrist.Length; i++)
            {
                rng.NextBytes(buf);
                CastlingZobrist[i] = BitConverter.ToUInt64(buf, 0);
            }
            for (int i = 0; i < EpZobrist.Length; i++)
            {
                rng.NextBytes(buf);
                EpZobrist[i] = BitConverter.ToUInt64(buf, 0);
            }
            rng.NextBytes(buf);
            SideToMoveZobrist = BitConverter.ToUInt64(buf, 0);
        }

        public Board() { LoadFEN(StartFEN); }

        public void LoadFEN(string fen)
        {
            Array.Fill(squares, Empty);
            var parts = fen.Split(' ');
            if (parts.Length < 4) throw new ArgumentException("Invalid FEN");

            int rank = 7, file = 0;
            foreach (var c in parts[0])
            {
                if (c == '/') { rank--; file = 0; continue; }
                if (char.IsDigit(c)) file += c - '0';
                else
                {
                    int sq = (rank << 4) | file;
                    squares[sq] = FenCharToPiece(c);
                    file++;
                }
            }

            WhiteKingSquare = -1; BlackKingSquare = -1;
            for (int s = 0; s < 128; s++)
            {
                if ((s & 0x88) != 0) continue;
                var p = squares[s];
                if (p == King) WhiteKingSquare = s;
                else if (p == (sbyte)(King + 6)) BlackKingSquare = s;
            }

            SideToMove = parts[1] == "w" ? White : Black;

            CastlingRights = 0;
            if (parts[2].Contains("K")) CastlingRights |= 1 << 0;
            if (parts[2].Contains("Q")) CastlingRights |= 1 << 1;
            if (parts[2].Contains("k")) CastlingRights |= 1 << 2;
            if (parts[2].Contains("q")) CastlingRights |= 1 << 3;

            EnPassant = -1;
            if (parts[3] != "-")
            {
                int epFile = parts[3][0] - 'a';
                int epRank = parts[3][1] - '1';
                EnPassant = (epRank << 4) | epFile;
            }

            HalfmoveClock = parts.Length > 4 ? int.Parse(parts[4]) : 0;
            FullmoveNumber = parts.Length > 5 ? int.Parse(parts[5]) : 1;

            ZobristKey = ComputeZobristFromBoard();
        }

        public string ToFEN()
        {
            var sb = new StringBuilder();
            for (int rank = 7; rank >= 0; rank--)
            {
                int empty = 0;
                for (int file = 0; file < 8; file++)
                {
                    int sq = (rank << 4) | file;
                    var piece = squares[sq];
                    if (piece == Empty) empty++;
                    else
                    {
                        if (empty > 0) { sb.Append(empty); empty = 0; }
                        sb.Append(PieceToFenChar(piece));
                    }
                }
                if (empty > 0) sb.Append(empty);
                if (rank > 0) sb.Append('/');
            }
            sb.Append(' ');
            sb.Append(SideToMove == White ? 'w' : 'b');
            sb.Append(' ');
            string cr = "";
            if ((CastlingRights & 1) != 0) cr += "K";
            if ((CastlingRights & 2) != 0) cr += "Q";
            if ((CastlingRights & 4) != 0) cr += "k";
            if ((CastlingRights & 8) != 0) cr += "q";
            sb.Append(cr.Length > 0 ? cr : "-");
            sb.Append(' ');
            if (EnPassant >= 0)
            {
                int file = EnPassant & 15;
                int rank = EnPassant >> 4;
                sb.Append((char)('a' + file));
                sb.Append((char)('1' + rank));
            }
            else sb.Append('-');
            sb.Append(' ');
            sb.Append(HalfmoveClock);
            sb.Append(' ');
            sb.Append(FullmoveNumber);
            return sb.ToString();
        }

        private static sbyte FenCharToPiece(char c)
        {
            return c switch
            {
                'P' => Pawn,
                'N' => Knight,
                'B' => Bishop,
                'R' => Rook,
                'Q' => Queen,
                'K' => King,
                'p' => (sbyte)(Pawn + 6),
                'n' => (sbyte)(Knight + 6),
                'b' => (sbyte)(Bishop + 6),
                'r' => (sbyte)(Rook + 6),
                'q' => (sbyte)(Queen + 6),
                'k' => (sbyte)(King + 6),
                _ => Empty
            };
        }

        private static char PieceToFenChar(sbyte piece)
        {
            return piece switch
            {
                Pawn => 'P',
                Knight => 'N',
                Bishop => 'B',
                Rook => 'R',
                Queen => 'Q',
                King => 'K',
                (Pawn + 6) => 'p',
                (Knight + 6) => 'n',
                (Bishop + 6) => 'b',
                (Rook + 6) => 'r',
                (Queen + 6) => 'q',
                (King + 6) => 'k',
                _ => ' '
            };
        }

        public struct Move
        {
            private readonly int value;
            public Move(int v) { value = v; }
            public static implicit operator int(Move m) => m.value;
            public static implicit operator Move(int v) => new Move(v);
            public static Move Create(int from, int to, int promo, bool capture, bool ep, bool castle, bool doublePush)
            {
                int v = (from & 127) | ((to & 127) << 7) | ((promo & 7) << 14);
                if (capture) v |= 1 << 17;
                if (ep) v |= 1 << 18;
                if (castle) v |= 1 << 19;
                if (doublePush) v |= 1 << 20;
                return new Move(v);
            }
            public static int GetFrom(Move m) => m.value & 127;
            public static int GetTo(Move m) => (m.value >> 7) & 127;
            public static int GetPromotion(Move m) => (m.value >> 14) & 7;
        }

        public struct Undo
        {
            public sbyte capturedPiece;
            public int oldEnPassant;
            public int oldCastlingRights;
            public int oldHalfmoveClock;
            public int oldFullmoveNumber;
            public int oldSideToMove;
            public int oldWhiteKingSquare;
            public int oldBlackKingSquare;
            public bool wasQuiet;
            public ulong oldZobristKey;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void MakeMoveWithUndo(Move move, out Undo undo)
        {
            int from = Move.GetFrom(move);
            int to = Move.GetTo(move);
            int promo = Move.GetPromotion(move);
            int mv = (int)move;

            bool isCapture = ((mv >> 17) & 1) != 0;
            bool isEp = ((mv >> 18) & 1) != 0;
            bool isCastle = ((mv >> 19) & 1) != 0;
            bool isDoublePush = ((mv >> 20) & 1) != 0;

            var sq = this.squares;
            sbyte movingPiece = sq[from];
            int movingColor = (movingPiece > 6) ? Board.Black : Board.White;
            int pawnDir = (movingColor == Board.White) ? 0x10 : -0x10;
            int capturedSquare = isEp ? (to - pawnDir) : to;

            undo = new Undo
            {
                capturedPiece = sq[capturedSquare],
                oldEnPassant = EnPassant,
                oldCastlingRights = CastlingRights,
                oldHalfmoveClock = HalfmoveClock,
                oldFullmoveNumber = FullmoveNumber,
                oldSideToMove = SideToMove,
                oldWhiteKingSquare = WhiteKingSquare,
                oldBlackKingSquare = BlackKingSquare,
                oldZobristKey = ZobristKey
            };

            if (movingPiece == King)
            {
                if (movingColor == Board.White) CastlingRights &= ~((1 << 0) | (1 << 1));
                else CastlingRights &= ~((1 << 2) | (1 << 3));
            }
            else if (movingPiece == Rook)
            {
                int fromRank = from >> 4, fromFile = from & 0xF;
                if (movingColor == Board.White)
                {
                    if (fromRank == 0 && fromFile == 0) CastlingRights &= ~(1 << 1);
                    if (fromRank == 0 && fromFile == 7) CastlingRights &= ~(1 << 0);
                }
                else
                {
                    if (fromRank == 7 && fromFile == 0) CastlingRights &= ~(1 << 3);
                    if (fromRank == 7 && fromFile == 7) CastlingRights &= ~(1 << 2);
                }
            }

            if (undo.capturedPiece == Rook || undo.capturedPiece == (Rook + 6))
            {
                int capRank = capturedSquare >> 4, capFile = capturedSquare & 0xF;
                if (undo.capturedPiece == Rook)
                {
                    if (capRank == 0 && capFile == 0) CastlingRights &= ~(1 << 1);
                    if (capRank == 0 && capFile == 7) CastlingRights &= ~(1 << 0);
                }
                else
                {
                    if (capRank == 7 && capFile == 0) CastlingRights &= ~(1 << 3);
                    if (capRank == 7 && capFile == 7) CastlingRights &= ~(1 << 2);
                }
            }

            bool isQuiet = !isCapture && !isEp && !isCastle && promo == 0;
            if (isQuiet)
            {
                undo.wasQuiet = true;
                sq[to] = sq[from];
                sq[from] = Empty;
                if (movingPiece == King) WhiteKingSquare = to;
                else if (movingPiece == (King + 6)) BlackKingSquare = to;

                EnPassant = -1;
                if (isDoublePush) EnPassant = from + pawnDir;

                if (movingPiece == Pawn || movingPiece == (Pawn + 6)) HalfmoveClock = 0; else HalfmoveClock++;
                SideToMove ^= 1;
                if (SideToMove == White) FullmoveNumber++;

                // INCREMENTAL ZOBRIST (O(1)) using previously stored key
                ZobristKey = UpdateZobristAfterMove(undo.oldZobristKey, from, to, movingPiece, undo.capturedPiece, promo, isEp, isCastle, isDoublePush, undo.oldEnPassant, undo.oldCastlingRights);
                return;
            }

            if (isCastle)
            {
                sq[from] = Empty;
                sq[to] = movingPiece;
                int rank = from >> 4;
                if (to == ((rank << 4) | 6))
                {
                    sq[(rank << 4) | 5] = sq[(rank << 4) | 7];
                    sq[(rank << 4) | 7] = Empty;
                }
                else if (to == ((rank << 4) | 2))
                {
                    sq[(rank << 4) | 3] = sq[(rank << 4) | 0];
                    sq[(rank << 4) | 0] = Empty;
                }
                if (movingColor == Board.White) WhiteKingSquare = to; else BlackKingSquare = to;
            }
            else
            {
                if (isEp) sq[capturedSquare] = Empty;
                sbyte placed = (promo != 0) ? (sbyte)(promo + (movingPiece > 6 ? 6 : 0)) : sq[from];
                sq[to] = placed;
                sq[from] = Empty;
                if (movingPiece == King) WhiteKingSquare = to;
                else if (movingPiece == (King + 6)) BlackKingSquare = to;
            }

            EnPassant = -1;
            if (isDoublePush) EnPassant = from + pawnDir;
            if (movingPiece == Pawn || movingPiece == (Pawn + 6) || undo.capturedPiece != Empty) HalfmoveClock = 0; else HalfmoveClock++;
            SideToMove ^= 1;
            if (SideToMove == White) FullmoveNumber++;

            // INCREMENTAL ZOBRIST (O(1))
            ZobristKey = UpdateZobristAfterMove(undo.oldZobristKey, from, to, movingPiece, undo.capturedPiece, promo, isEp, isCastle, isDoublePush, undo.oldEnPassant, undo.oldCastlingRights);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void UnmakeMove(Move move, Undo undo)
        {
            int from = Move.GetFrom(move);
            int to = Move.GetTo(move);
            int promo = Move.GetPromotion(move);
            int mv = (int)move;
            bool isPromotion = promo != 0;
            bool isEnPassant = ((mv >> 18) & 1) != 0;
            bool isCastle = ((mv >> 19) & 1) != 0;

            var sq = this.squares;
            if (undo.wasQuiet)
            {
                sq[from] = sq[to];
                sq[to] = Empty;
                if (sq[from] == Board.King) WhiteKingSquare = undo.oldWhiteKingSquare;
                else if (sq[from] == (Board.King + 6)) BlackKingSquare = undo.oldBlackKingSquare;

                EnPassant = undo.oldEnPassant;
                CastlingRights = undo.oldCastlingRights;
                HalfmoveClock = undo.oldHalfmoveClock;
                FullmoveNumber = undo.oldFullmoveNumber;
                SideToMove = undo.oldSideToMove;

                ZobristKey = undo.oldZobristKey;
                return;
            }

            sbyte movedPiece = sq[to];

            if (isPromotion)
            {
                sbyte pawn = (movedPiece > 6) ? (sbyte)(Pawn + 6) : Pawn;
                sq[from] = pawn;
                sq[to] = undo.capturedPiece;
            }
            else if (isEnPassant)
            {
                sq[from] = sq[to];
                sq[to] = Empty;
                int capSq = (undo.oldSideToMove == Board.White) ? (to - 0x10) : (to + 0x10);
                sq[capSq] = undo.capturedPiece;
            }
            else if (isCastle)
            {
                sq[from] = sq[to];
                sq[to] = Empty;
                int rank = from >> 4;
                if (to == ((rank << 4) | 6))
                {
                    sq[(rank << 4) | 7] = sq[(rank << 4) | 5];
                    sq[(rank << 4) | 5] = Empty;
                }
                else if (to == ((rank << 4) | 2))
                {
                    sq[(rank << 4) | 0] = sq[(rank << 4) | 3];
                    sq[(rank << 4) | 3] = Empty;
                }
            }
            else
            {
                sq[from] = sq[to];
                sq[to] = undo.capturedPiece;
            }

            EnPassant = undo.oldEnPassant;
            CastlingRights = undo.oldCastlingRights;
            HalfmoveClock = undo.oldHalfmoveClock;
            FullmoveNumber = undo.oldFullmoveNumber;
            SideToMove = undo.oldSideToMove;
            WhiteKingSquare = undo.oldWhiteKingSquare;
            BlackKingSquare = undo.oldBlackKingSquare;

            ZobristKey = undo.oldZobristKey;
        }

        private ulong ComputeZobristFromBoard()
        {
            ulong h = 0UL;
            for (int s = 0; s < 128; s++)
            {
                if ((s & 0x88) != 0) continue;
                sbyte p = squares[s];
                if (p == Empty) continue;
                int idx = p - 1;
                h ^= PieceZobrist[idx, s];
            }
            h ^= CastlingZobrist[CastlingRights & 0xF];
            if (EnPassant >= 0) h ^= EpZobrist[EnPassant & 0x7F];
            if (SideToMove == Black) h ^= SideToMoveZobrist;
            return h;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int PieceIndex(sbyte p) => p == 0 ? -1 : (p - 1);

        private ulong UpdateZobristAfterMove(ulong oldKey, int from, int to, sbyte movingPiece, sbyte capturedPiece, int promo, bool isEp, bool isCastle, bool isDoublePush, int oldEp, int oldCastling)
        {
            ulong key = oldKey;
            // toggle side
            key ^= SideToMoveZobrist;

            int movingIdx = PieceIndex(movingPiece);
            if (movingIdx >= 0) key ^= PieceZobrist[movingIdx, from];

            if (capturedPiece != Empty)
            {
                int capSq = isEp ? (to - ((movingPiece > 6) ? 0x10 : -0x10)) : to;
                int capIdx = PieceIndex(capturedPiece);
                if (capIdx >= 0) key ^= PieceZobrist[capIdx, capSq];
            }

            if (promo != 0)
            {
                sbyte promotedPiece = (sbyte)(promo + (movingPiece > 6 ? 6 : 0));
                int promIdx = PieceIndex(promotedPiece);
                if (promIdx >= 0) key ^= PieceZobrist[promIdx, to];
            }
            else
            {
                if (movingIdx >= 0) key ^= PieceZobrist[movingIdx, to];
            }

            // handle castling rook movement: XOR rook-from and rook-to squares
            if (isCastle)
            {
                int rank = from >> 4;
                if (to == ((rank << 4) | 6))
                {
                    int rookFrom = (rank << 4) | 7;
                    int rookTo = (rank << 4) | 5;
                    sbyte rookPiece = (sbyte)(Rook + (movingPiece > 6 ? 6 : 0));
                    int rookIdx = PieceIndex(rookPiece);
                    if (rookIdx >= 0)
                    {
                        key ^= PieceZobrist[rookIdx, rookFrom];
                        key ^= PieceZobrist[rookIdx, rookTo];
                    }
                }
                else if (to == ((rank << 4) | 2))
                {
                    int rookFrom = (rank << 4) | 0;
                    int rookTo = (rank << 4) | 3;
                    sbyte rookPiece = (sbyte)(Rook + (movingPiece > 6 ? 6 : 0));
                    int rookIdx = PieceIndex(rookPiece);
                    if (rookIdx >= 0)
                    {
                        key ^= PieceZobrist[rookIdx, rookFrom];
                        key ^= PieceZobrist[rookIdx, rookTo];
                    }
                }
            }

            // en-passant: remove old ep XOR; add new ep XOR if double push
            if (oldEp >= 0) key ^= EpZobrist[oldEp & 0x7F];
            if (isDoublePush)
            {
                int newEp = from + ((movingPiece > 6) ? -0x10 : 0x10);
                key ^= EpZobrist[newEp & 0x7F];
            }

            // castling rights delta
            key ^= CastlingZobrist[oldCastling & 0xF];
            key ^= CastlingZobrist[CastlingRights & 0xF];

            return key;
        }
    }
}

// Program.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading.Tasks;

namespace PromethiusEngine
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var engine = new UciEngine();
            engine.Run();
        }
    }

    public class UciEngine
    {
        private bool _isRunning = true;
        private Board _board = new Board();
        private Dictionary<string, string> _options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _bookActive = true;
        // Track current background search task so Stop can request cancellation
        private Task? _searchTask = null;

        public UciEngine()
        {
            // Safer default while tuning: 256 MiB. Increase to (1L<<30) for 1 GiB once tuned.
            TranspositionTable.Initialize(1L << 28); // 256 MiB
            try
            {
                OpeningBook.Load("Games.txt");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Promethius] OpeningBook.Load failed: {ex.Message}");
            }

            _bookActive = true;
        }

        public void Run()
        {
            string? line;
            while (_isRunning && (line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (line == string.Empty) continue;
                Console.Error.WriteLine($"[Promethius] recv: {line}");

                if (line == "uci") HandleUci();
                else if (line == "isready") HandleIsReady();
                else if (line.StartsWith("setoption")) HandleSetOption(line);
                else if (line == "ucinewgame") HandleUciNewGame();
                else if (line.StartsWith("position")) HandlePosition(line);
                else if (line.StartsWith("go")) HandleGo(line);
                else if (line == "stop") HandleStop();
                else if (line == "ponderhit") HandlePonderHit();
                else if (line == "quit") HandleQuit();
                else if (line == "debug") HandleDebug(line);
                else if (line.StartsWith("register")) HandleRegister(line);
                else if (line.StartsWith("perft")) HandlePerft(line);
                else if (line.StartsWith("bulktest")) HandleBulkTest(line);
                else if (line.StartsWith("checkfen")) HandleCheckFen(line);
                else
                {
                    Console.Error.WriteLine($"[Promethius] Unknown command: {line}");
                }
            }
        }

        private void HandleCheckFen(string line)
        {
            var rest = line.Substring("checkfen".Length).Trim();
            if (string.IsNullOrEmpty(rest)) { Console.Error.WriteLine("usage: checkfen <FEN>"); return; }
            try
            {
                var b = new Board();
                b.LoadFEN(rest);
                Span<Board.Move> fastSpan = stackalloc Board.Move[218];
                int fastCount;
                MoveGenerator.GenerateMovesSpan(b, fastSpan, out fastCount);
                var brute = MoveGenerator.GenerateMovesBruteforce(b);
                Console.Error.WriteLine($"fast count = {fastCount}, brute count = {brute.Count}");
                // Print pins/checkers for debugging
                bool[] oppAttacked; int[] pinDir; List<int> checkers;
                MoveGenerator.ComputeAttacksPinsForDebug(b, b.SideToMove, out oppAttacked, out pinDir, out checkers);
                Console.Error.WriteLine($"King square: {(b.SideToMove == Board.White ? b.WhiteKingSquare : b.BlackKingSquare)}");
                int kingSq = (b.SideToMove == Board.White) ? b.WhiteKingSquare : b.BlackKingSquare;
                Console.Error.WriteLine("oppAttacked around king:");
                ReadOnlySpan<int> deltas = stackalloc int[8] { 0x10, 0x11, 0x01, -0x0f, -0x10, -0x11, -0x01, 0x0f };
                foreach (var d in deltas)
                {
                    int to = kingSq + d;
                    if ((to & 0x88) != 0) continue;
                    Console.Error.WriteLine($"  sq={to} attacked={oppAttacked[to]}");
                }
                Console.Error.WriteLine("Checkers: " + string.Join(',', checkers));
                for (int i = 0; i < pinDir.Length; i++) if (pinDir[i] != 0) Console.Error.WriteLine($"pinned: {i} dir={pinDir[i]}");
                for (int i = 0; i < fastCount; i++) Console.WriteLine("fast: " + MoveToUci(fastSpan[i]));
                foreach (var mv in brute) Console.WriteLine("brute: " + MoveToUci(mv));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("checkfen error: " + ex.Message);
            }
        }

        private void HandleUci()
        {
            Console.WriteLine("id name Promethius");
            Console.WriteLine("id author ChatGPT");
            Console.WriteLine("uciok");
        }

        private void HandleIsReady()
        {
            Console.WriteLine("readyok");
        }

        private void HandleSetOption(string line)
        {
            var m = Regex.Match(line, "setoption\\s+name\\s+(.+?)(\\s+value\\s+(.+))?$");
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                var value = m.Groups[3].Success ? m.Groups[3].Value.Trim() : string.Empty;
                _options[name] = value;
                Console.Error.WriteLine($"[Promethius] setoption: {name} = {value}");
            }
            else Console.Error.WriteLine("[Promethius] setoption parse failed: " + line);
        }

        private void HandleUciNewGame()
        {
            _board = new Board();
            _board.LoadFEN(Board.StartFEN);
            _bookActive = true; // re-enable book usage for new game
            Console.Error.WriteLine("[Promethius] New game: board reset to startpos.");
        }


        private void HandlePosition(string line)
        {
            var rest = line.Substring("position".Length).Trim();

            if (rest.StartsWith("startpos"))
            {
                _board.LoadFEN(Board.StartFEN);
                rest = rest.Substring("startpos".Length).Trim();
            }
            else if (rest.StartsWith("fen"))
            {
                rest = rest.Substring("fen".Length).Trim();
                var movesIndex = IndexOfMovesToken(rest);
                if (movesIndex >= 0)
                {
                    var fen = rest.Substring(0, movesIndex).Trim();
                    _board.LoadFEN(fen);
                    rest = rest.Substring(movesIndex).Trim();
                }
                else
                {
                    var fen = rest.Trim();
                    _board.LoadFEN(fen);
                    rest = string.Empty;
                }
            }

            if (rest.StartsWith("moves"))
            {
                var tokens = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < tokens.Length; i++)
                {
                    var mv = tokens[i].Trim();
                    if (mv.Length >= 4)
                    {
                        bool ok = ApplyUciMove(_board, mv);
                        if (!ok)
                            Console.Error.WriteLine($"[Promethius] Failed to apply move: {mv}");
                    }
                }
            }

            Console.Error.WriteLine($"[Promethius] position applied. FEN: {_board.ToFEN()}");
        }

        private int IndexOfMovesToken(string s)
        {
            var m = Regex.Match(s, "\\s+moves\\s+", RegexOptions.IgnoreCase);
            if (m.Success) return m.Index;
            if (s.StartsWith("moves")) return 0;
            return -1;
        }

        private bool ApplyUciMove(Board board, string uciMove)
        {
            try
            {
                // convert UCI (e2e4) to 0x88 indexing: sq = (rank<<4) | file
                int fromFile = uciMove[0] - 'a';
                int fromRank = uciMove[1] - '1';
                int toFile = uciMove[2] - 'a';
                int toRank = uciMove[3] - '1';
                int from = (fromRank << 4) | fromFile;
                int to = (toRank << 4) | toFile;

                byte promo = 0;
                if (uciMove.Length >= 5)
                {
                    char p = char.ToLowerInvariant(uciMove[4]);
                    promo = p switch { 'n' => 2, 'b' => 3, 'r' => 4, 'q' => 5, _ => 0 };
                }

                sbyte movingPiece = board.Squares[from];
                bool capture = false;
                bool ep = false;
                bool castle = false;
                bool doublePush = false;

                if (movingPiece == Board.Pawn || movingPiece == (sbyte)(Board.Pawn + 6))
                {
                    if (Math.Abs(to - from) == 0x20) doublePush = true; // 2 ranks = 0x20 in 0x88
                    if (to == board.EnPassant) { ep = true; capture = true; }
                }

                if (board.Squares[to] != Board.Empty) capture = true;

                if (movingPiece == Board.King || movingPiece == (sbyte)(Board.King + 6))
                {
                    int fromFileF = from & 0xF;
                    int toFileF = to & 0xF;
                    if (Math.Abs(toFileF - fromFileF) == 2) castle = true;
                }

                var move = Board.Move.Create(from, to, promo, capture, ep, castle, doublePush);
                board.MakeMoveWithUndo(move, out var undo);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Promethius] ApplyUciMove exception: {ex.Message}");
                return false;
            }
        }

        private void HandleGo(string line)
        {
            Console.Error.WriteLine("[Promethius] go command received: " + line);

            // FIRST: try the opening book BEFORE allocating time / starting search
            try
            {
                if (OpeningBook.TryGetRandomMove(_board, out var bookMove))
                {
                    var uci = MoveToUci(bookMove);
                    Console.Error.WriteLine($"[Promethius] using opening book move: {uci}");
                    // also print an info string so GUI/logs can see the engine used the book
                    Console.WriteLine($"info string using opening book move: {uci}");
                    // return book move as UCI bestmove and skip search entirely
                    Console.WriteLine("bestmove " + uci);
                    return;
                }
                else
                {
                    // first time we fail to find a book move, log that book usage has ended
                    if (_bookActive)
                    {
                        _bookActive = false;
                        Console.Error.WriteLine("[Promethius] opening book exhausted for this position — switching to search.");
                        Console.WriteLine("info string opening book exhausted, switching to search");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Promethius] OpeningBook.TryGetRandomMove exception: {ex.Message}");
                // continue to search normally
            }

            // --- existing go handling below (time parsing, search)
            // Parse UCI go parameters
            var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            long wtime = -1, btime = -1, movetime = -1, winc = 0, binc = 0;
            int movestogo = 40;

            for (int i = 1; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case "wtime": if (i + 1 < tokens.Length) long.TryParse(tokens[++i], out wtime); break;
                    case "btime": if (i + 1 < tokens.Length) long.TryParse(tokens[++i], out btime); break;
                    case "movetime": if (i + 1 < tokens.Length) long.TryParse(tokens[++i], out movetime); break;
                    case "movestogo": if (i + 1 < tokens.Length) int.TryParse(tokens[++i], out movestogo); break;
                    case "winc": if (i + 1 < tokens.Length) long.TryParse(tokens[++i], out winc); break;
                    case "binc": if (i + 1 < tokens.Length) long.TryParse(tokens[++i], out binc); break;
                }
            }

            // Use 90% of the allocated time for thinking per requirement
            int allocMs = 0;
            const int maxAllocMs = 10_000; // 10 seconds
            const int minAllocMs = 2500; // 2.5 seconds

            if (movetime > 0)
            {
                allocMs = (int)Math.Min(maxAllocMs, movetime);
            }
            else
            {
                long myTime = (_board.SideToMove == Board.White) ? wtime : btime;
                long myInc = (_board.SideToMove == Board.White) ? winc : binc;

                if (myTime > 0)
                {
                    double estMoves = Math.Max(1, movestogo);
                    double baseAlloc = (double)myTime / estMoves + myInc;
                    int clamped = (int)Math.Round(baseAlloc);
                    if (clamped > maxAllocMs) clamped = maxAllocMs;
                    if (myTime < minAllocMs) clamped = (int)myTime;
                    else if (clamped < minAllocMs) clamped = minAllocMs;
                    allocMs = clamped;
                }
                else
                {
                    allocMs = 5000;
                }
            }

            int thinkMs = (int)(allocMs * 0.9);
            if (thinkMs < 50) thinkMs = allocMs;

            Console.Error.WriteLine($"[Promethius] time alloc: {allocMs}ms, thinking for {thinkMs}ms (sideToMove={_board.SideToMove})");

            int maxDepth = 30;
            // Run search asynchronously so the UCI main loop can process 'stop'
            Search.ClearStop();
            // copy board by FEN so the search can run without locking the main board
            var boardForSearch = new Board();
            boardForSearch.LoadFEN(_board.ToFEN());
            _searchTask = Task.Run(() =>
            {
                try
                {
                    var best = Search.SearchBestMove(boardForSearch, maxDepth, thinkMs);
                    if ((int)best == 0) Console.WriteLine("bestmove 0000");
                    else Console.WriteLine("bestmove " + MoveToUci(best));
                }
                catch (OperationCanceledException)
                {
                    // Search was cancelled: best move may have been printed earlier or none available
                    Console.Error.WriteLine("[Promethius] search cancelled");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Promethius] search exception: {ex.Message}");
                }
            });
        }


        private void HandleStop()
        {
            Console.Error.WriteLine("[Promethius] stop requested");
            Search.RequestStop();
            try
            {
                // wait briefly for search task to acknowledge stop
                if (_searchTask != null) _searchTask.Wait(500);
            }
            catch (AggregateException) { }
        }
        private void HandlePonderHit() { }
        private void HandleQuit() { _isRunning = false; }
        private void HandleDebug(string line) { Console.Error.WriteLine($"[Promethius] debug: {line}"); }
        private void HandleRegister(string line) { Console.Error.WriteLine($"[Promethius] register requested: {line}"); }

        private void HandlePerft(string line)
        {
            try
            {
                var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) { Console.Error.WriteLine("[Promethius] perft: missing depth"); return; }

                bool doDivide = false;
                int depth;

                if (tokens.Length == 2)
                {
                    if (!int.TryParse(tokens[1], out depth)) { Console.Error.WriteLine("[Promethius] perft: invalid depth"); return; }
                }
                else if (tokens.Length >= 3 && tokens[1].Equals("divide", StringComparison.OrdinalIgnoreCase))
                {
                    doDivide = true;
                    if (!int.TryParse(tokens[2], out depth)) { Console.Error.WriteLine("[Promethius] perft divide: invalid depth"); return; }
                }
                else
                {
                    Console.Error.WriteLine("[Promethius] perft: unrecognized format");
                    return;
                }

                Console.Error.WriteLine($"[Promethius] running perft (depth={depth}) on current position...");
                var sw = Stopwatch.StartNew();
                // Keep diagnostics off for normal perft runs to maximize speed
                MoveGenerator.DiagnosticsEnabled = false;

                if (!doDivide)
                {
                    long nodes = MoveGenerator.Perft(_board, depth);
                    sw.Stop();
                    MoveGenerator.DiagnosticsEnabled = false;
                    double seconds = Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                    double nps = nodes / seconds;
                    Console.WriteLine($"perft nodes {nodes}");
                    Console.Error.WriteLine($"[Promethius] perft depth={depth} nodes={nodes} time={sw.Elapsed.TotalMilliseconds:F1}ms nps={nps:N0}");
                }
                else
                {
                    var results = MoveGenerator.PerftDivide(_board, depth);
                    long total = 0;
                    foreach (var (move, nodes) in results)
                    {
                        string uci = MoveToUci(move);
                        Console.WriteLine($"{uci}: {nodes}");
                        total += nodes;
                    }
                    sw.Stop();
                    double seconds = Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                    double nps = total / seconds;
                    Console.WriteLine($"perft total {total}");
                    Console.Error.WriteLine($"[Promethius] perft divide depth={depth} total={total} time={sw.Elapsed.TotalMilliseconds:F1}ms nps={nps:N0}");
                }
                // Diagnostic: when doing divide, compare fast move generation with brute-force validation
                if (doDivide && depth >= 1)
                {
                    Span<Board.Move> fastList = stackalloc Board.Move[218];
                    int fastListCount;
                    MoveGenerator.GenerateMovesSpan(_board, fastList, out fastListCount);
                    var brute = MoveGenerator.GenerateMovesBruteforce(_board);
                    var bruteSet = new HashSet<int>(brute.ConvertAll(m => (int)m));
                    // moves in fast but not in brute
                    for (int i = 0; i < fastListCount; i++)
                    {
                        var mv = fastList[i];
                        if (!bruteSet.Contains((int)mv))
                        {
                            Console.Error.WriteLine($"[Promethius] Illegal fast move: {MoveToUci(mv)}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Promethius] perft exception: {ex.Message}");
            }
        }

        private void HandleBulkTest(string line)
        {
            // Define the test cases: FEN and expected node counts per depth
            var tests = new List<(string fen, Dictionary<int, long> expected)>()
            {
                ("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", new Dictionary<int,long> {{1,20},{2,400},{3,8902},{4,197281},{5,4865609}}),
                ("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", new Dictionary<int,long> {{1,48},{2,2039},{3,97862},{4,4085603}}),
                ("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", new Dictionary<int,long> {{1,14},{2,191},{3,2812},{4,43238},{5,674624},{6,11030083}}),
                ("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", new Dictionary<int,long> {{1,6},{2,264},{3,9467},{4,422333},{5,15833292}}),
                ("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", new Dictionary<int,long> {{1,44},{2,1486},{3,62379},{4,2103487}}),
                ("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", new Dictionary<int,long> {{1,46},{2,2079},{3,89890},{4,3894594}}),
            };

            Console.Error.WriteLine("[Promethius] running bulk perft tests...");
            foreach (var (fen, expected) in tests)
            {
                try
                {
                    var b = new Board();
                    b.LoadFEN(fen);
                    Console.Error.WriteLine($"[Promethius] Test FEN: {fen}");
                    foreach (var kv in expected)
                    {
                        int depth = kv.Key;
                        long want = kv.Value;
                        var sw = Stopwatch.StartNew();
                        // Enable deep diagnostics for this perft run so mismatches surface
                        MoveGenerator.DiagnosticsEnabled = true;
                        long nodes = MoveGenerator.Perft(b, depth);
                        MoveGenerator.DiagnosticsEnabled = false;
                        sw.Stop();
                        double seconds = Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                        double nps = nodes / seconds;
                        Console.WriteLine($"FEN depth={depth} nodes={nodes} time={sw.Elapsed.TotalMilliseconds:F1}ms nps={nps:N0}");
                        if (nodes != want)
                        {
                            Console.Error.WriteLine($"[Promethius] MISMATCH for FEN (depth={depth}): expected={want} got={nodes}");
                        }
                        else
                        {
                            Console.Error.WriteLine($"[Promethius] OK depth={depth} nodes={nodes} time={sw.Elapsed.TotalMilliseconds:F1}ms nps={nps:N0}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Promethius] bulktest error for fen '{fen}': {ex.Message}");
                }
            }
            Console.Error.WriteLine("[Promethius] bulk perft tests complete.");
        }

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
    }
}

Promethius — a compact learning chess engine in C#

Overview

Promethius is a small, readable chess engine implemented using a 0x88 board representation. It is intended for experimentation and learning rather than immediate tournament strength. The codebase demonstrates core engine components and common search/evaluation techniques in a concise form.

Current highlights

- UCI-compatible interface with informative `info` output (depth, time, nodes, nps, pv).
- Iterative-deepening negamax search with alpha-beta pruning and PV extraction.
- Quiescence search at leaf nodes (captures/promotions) to reduce horizon effects.
- Move ordering pipeline including:
  - Root PV hint (try previous-iteration PV first)
  - Transposition-table suggested move
  - MVV-LVA ordering for captures (Most Valuable Victim — Least Valuable Attacker)
  - Promotions prioritized over other quiet moves
  - Killer moves (two killers per ply)
  - History heuristic for quiet moves
- Transposition table (store/probe with exact/upper/lower flags)
- Fast legal move generation (span-based API) and capture-only helper (`MoveGenerator.GenerateCaptures`).
- Lightweight material evaluation with a small pawn-advance bonus.

Files of interest

- `Board.cs` — 0x88 board, move encoding, make/unmake, Zobrist hashing
- `GenerateMoves.cs` — fast legal move generation, capture-only generator, perft helpers
- `Search.cs` — iterative deepening, negamax alpha-beta, quiescence, move ordering, TT integration
- `TranspositionTable.cs` — simple fixed-size TT with probe/store APIs
- `Evaluate.cs` — material-only evaluator (centipawns)
- `Program.cs` — UCI loop and command handling (opening book integration)
- `Games.txt` — plain opening-book file used for early moves

Building

Requires .NET SDK (the project currently targets .NET 9). To build locally:

```pwsh
dotnet build "Promethius.sln" -c Release
```

Running

Run the engine and connect a UCI GUI, or use the command-line for simple tests:

```pwsh
dotnet run --project Promethius.csproj
```

Example UCI behavior

- When searching, Promethius prints UCI info lines like:

  info depth 3 time 123 nodes 456 nps 3700 pv e2e4 e7e5 g1f3

  These help GUIs and log analyzers track progress (depth, elapsed ms, nodes searched, nodes/sec and principal variation).

Testing and diagnostics

- Use the built-in `perft` and `perft divide` utilities in the UCI/command loop to validate move generation.
- The engine exposes `NodesSearched` and prints diagnostics to stderr; you can compare node counts before/after enabling ordering or quiescence.

Development notes and next steps

- The current evaluator is intentionally simple (material + pawn advance). Improving evaluation (positional tables, mobility, king safety) will significantly change play.
- Move ordering already implements several heuristics; adding SEE pruning and better MVV-LVA tables will further reduce node-counts in tactical positions.
- The Transposition Table is used for ordering and storing lower bounds on cutoffs — extending it to return exact values when available will speed up alpha-beta.
- Tests to add: unit tests for perft positions and a small test harness to measure nodes reduction as ordering heuristics are enabled.

Contributing

Contributions and PRs are welcome. Good follow-ups:
- Improve evaluation and add tuning harness
- Implement SEE and MVV-LVA lookup tables for faster ordering
- Add more UCI options (hash size, asp windows, multi-threading)

License

This project is released under the MIT License. See `LICENSE` for details.

Acknowledgements

Promethius collects common chess-engine concepts (0x88, quiescence, alpha-beta, PV/TT, MVV-LVA) into a compact codebase for learning and experimentation.

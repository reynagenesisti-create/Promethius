Promethius — a small chess engine in C#

Overview

Promethius is a compact chess engine written in C# using a 0x88 board representation. The project is intended as a learning engine and a base for incremental engine development. It focuses on clarity and small, fast primitives (move generation, incremental make/unmake, simple evaluator) while progressively adding core search features.

Current features

- 0x88 board representation and compact move encoding
- Fast legal move generation with a span-based API and a brute-force validator
- UCI interface with info logging (prints depth, time, nodes, nps and PV lines)
- Iterative deepening search using negamax + alpha-beta
- Principal variation support and PV output per depth
- Quiescence search at leaf nodes (captures & promotions) to reduce horizon effects
- Move ordering pipeline (cheap scoring function combining):
	- previous-depth root PV (try PV first)
	- transposition-table best move (probe and try first)
	- MVV-LVA ordering for captures (most valuable victim / least valuable attacker)
	- promotions prioritized
	- killers (two killers per ply)
	- history heuristic for quiet moves
- Transposition table with simple probe/store API and age handling
- Opening book loader (random book move chooser from `Games.txt`)
- Perft and perft-divide utilities for move-generation validation
- Helper utilities in `MoveGenerator` (GenerateCaptures, IsInCheck, IsInCheckmate, IsInStalemate,
	IsFiftyMoveDraw, IsInsufficientMaterial, IsRepeatedPosition, IsDraw, HasLegalMoves)
- Simple, fast material-only evaluator (pawn=100, knight=320, bishop=330, rook=500, queen=900)

Why these choices

The engine prioritizes fast, easy-to-understand primitives while adding search features that provide large practical gains: quiescence to avoid horizon effects, MVV-LVA to order captures (big win for alpha-beta), and lightweight history/killers to promote quiet moves that cause cutoffs. These choices keep per-node ordering cheap while improving pruning.

Building

Requires .NET SDK (7.0 / 8.0). To build locally:

```pwsh
dotnet build "Promethius.sln"
```

Running

Run the engine from the command line to use the UCI interface:

```pwsh
dotnet run --project Promethius.csproj
```

Then connect a UCI GUI (e.g., Arena, CuteChess GUI) to the engine using the generated executable.

Repository layout

- `Board.cs` — board representation, move encoding, make/unmake and Zobrist hashing
- `GenerateMoves.cs` — fast legal move generation, capture-only generator, perft helpers
- `Search.cs` — iterative-deepening negamax + alpha-beta, PV, quiescence, move ordering, TT
- `Evaluate.cs` — simple material evaluator and tiny pawn-advance bonus
- `TranspositionTable.cs` — probe/store API and aging
- `SlidingAttackTables.cs` — precomputed sliding attack helpers
- `Program.cs` — UCI loop, command parsing and opening-book interaction
- `Games.txt` — opening book used by the engine

Contributing

Contributions are welcome. Good next steps:
- Improve evaluation (piece-square tables, mobility, king safety)
- Add SEE (static exchange evaluation) and stronger capture ordering
- Use the transposition table more aggressively (cutoffs using exact/upper/lower bounds)
- Add unit tests for move generation and perft cases; measure nodes reduction when adding heuristics

License

This project is released under the MIT License. See `LICENSE` for details.

Acknowledgements

This engine is intended for learning and experimentation. It implements many standard engine techniques (0x88, sliding attacks, quiescence, MVV-LVA, killers, history) in a compact form for educational use.

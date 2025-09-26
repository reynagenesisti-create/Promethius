Promethius — a small chess engine in C#

Overview

Promethius is a compact chess engine written in C# using a 0x88 board representation. It supports legal move generation, perft testing, a simple UCI interface, an opening book loader, and a placeholder search that currently selects a random legal move. The project is intended as a learning engine or a base for further engine development.

Features

- 0x88 board representation and move encoding
- Fast legal move generation (span-based API + brute-force validation)
- Perft and perft divide utilities
- UCI interface for connecting to GUIs
- Opening book support (simple random book move chooser)
- Stubbed search API to wire into UCI (`Search.SearchBestMove` returns a random legal move for now)

Building

Requires .NET SDK (7.0+ / 8.0+). To build locally:

```pwsh
dotnet build "Promethius.sln"
```

Running

You can run the engine from the command line to use the UCI interface:

```pwsh
dotnet run --project Promethius.csproj
```

Then connect a UCI GUI (e.g., Arena, Cute Chess GUI, or CuteChess) to the engine using the generated executable.

Repository layout

- `Board.cs` — board representation, move encoding, make/unmake and Zobrist hashing
- `GenerateMoves.cs` — fast legal move generation, perft helpers
- `Search.cs` — search entrypoint (currently returns a random legal move)
- `Evaluate.cs`, `Search.cs`, `TranspositionTable.cs`, `SlidingAttackTables.cs` — engine components
- `Program.cs` — UCI loop and command handling
- `Games.txt` — naive opening book used by the engine

Contributing

Contributions are welcome. Good next steps:
- Implement a proper search (iterative deepening, alpha-beta, PV & aspiration windows)
- Add evaluation improvements and tuning
- Add unit tests for move generation and perft cases

License

This project is released under the MIT License. See `LICENSE` for details.

Acknowledgements

This engine code is intended for learning and experimentation. It borrows common engine ideas (0x88, sliding attack tables, perft) used in many open-source engines.

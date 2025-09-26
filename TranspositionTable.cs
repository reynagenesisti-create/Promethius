using System;
using System.Runtime.InteropServices;

namespace PromethiusEngine
{
    public static class TranspositionTable
    {
        public const byte FlagExact = 0;
        public const byte FlagUpper = 1;
        public const byte FlagLower = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Entry
        {
            public ulong Key;
            public int Value;
            public int Move;
            public int Depth;
            public byte Flags;
            public byte Age;
        }

        // fixed: initialize with null-forgiving so the nullable-reference warning disappears
        private static Entry[] s_table = null!;
        private static int s_mask;
        private static byte s_age = 1;

        public static void Initialize(long targetBytes)
        {
            int entrySize = Marshal.SizeOf(typeof(Entry));
            long entries = Math.Max(1, targetBytes / entrySize);
            long size = 1L;
            while (size * 2 <= entries) size *= 2;
            s_table = new Entry[size];
            s_mask = (int)(size - 1);
            s_age = 1;
            Console.Error.WriteLine($"[Promethius] TT initialized: approx {(size * entrySize) / (1024.0 * 1024.0):F1} MiB, entries={size}");
        }

        public static void NewSearch()
        {
            s_age++;
            if (s_age == 0)
            {
                Array.Clear(s_table, 0, s_table.Length);
                s_age = 1;
            }
        }

        public static bool Probe(ulong key, out int value, out int depth, out int move, out byte flags)
        {
            if (s_table == null) { value = depth = move = 0; flags = 0; return false; }
            int idx = (int)(key & (ulong)s_mask);
            var e = s_table[idx];
            if (e.Key == key)
            {
                value = e.Value; depth = e.Depth; move = e.Move; flags = e.Flags; return true;
            }
            value = depth = move = 0; flags = 0; return false;
        }

        public static void Store(ulong key, int value, int depth, byte flags, int move)
        {
            if (s_table == null) return;
            int idx = (int)(key & (ulong)s_mask);
            var existing = s_table[idx];
            if (existing.Key == 0 || existing.Key == key || depth >= existing.Depth)
            {
                s_table[idx].Key = key;
                s_table[idx].Value = value;
                s_table[idx].Depth = depth;
                s_table[idx].Move = move;
                s_table[idx].Flags = flags;
                s_table[idx].Age = s_age;
            }
        }
    }
}

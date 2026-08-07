using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Evolution.Base
{
    public class Brain
    {
        // [id, level, id, level, ...]
        public List<byte> Data { get; } = new List<byte>();

        // Pair-wise accessors so callers (inheritance, mutation) can walk the
        // brain without knowing about the flat byte layout.
        public int AbilityCount => Data.Count / 2;
        public byte IdAt(int index) => Data[index * 2];
        public byte LevelAt(int index) => Data[index * 2 + 1];

        public void AddAbility(byte id, byte level)
        {
            for (int i = 0; i < Data.Count; i += 2)
            {
                if (Data[i] == id)
                {
                    if (level > Data[i + 1]) Data[i + 1] = level;
                    return;
                }
            }
            Data.Add(id);
            Data.Add(level);
        }

        public byte? GetLevel(byte id)
        {
            for (int i = 0; i < Data.Count; i += 2)
                if (Data[i] == id) return Data[i + 1];
            return null;
        }

        public bool Has(byte id) => GetLevel(id).HasValue;

    }
}


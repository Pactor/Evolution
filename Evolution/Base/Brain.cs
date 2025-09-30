using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Rebar;

namespace Evolution.Base
{
    public class Brain
    {
        // [id, level, id, level, ...]
        public List<byte> Data { get; } = new List<byte>();

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


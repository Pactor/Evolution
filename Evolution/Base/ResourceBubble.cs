using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Evolution.Base
{
    public class ResourceBubble
    {
        public Rectangle Bounds { get; set; }
        public int Value { get; set; } = 100;   // natural: 100; farmed: 500
        public int? OwnerTeamId { get; set; }   // null = neutral
        public ResourceBubble(Rectangle r, int? owner = null, int startValue = 100)
        {
            Bounds = r; OwnerTeamId = owner; Value = startValue;
        }
    }
}

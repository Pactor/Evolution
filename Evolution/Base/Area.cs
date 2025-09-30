using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Evolution.Base
{
    public class Area
    {
        public Rectangle Bounds { get; set; }
        public Brush Fill { get; set; }
        public Pen Outline { get; set; }

        // Food/Water (resource pools)
        public List<ResourceBubble> Bubbles { get; set; } = new List<ResourceBubble>();

        // Poison/Forest/Desert (static)
        public List<Rectangle> StaticBubbles { get; set; } = new List<Rectangle>();
    }

}

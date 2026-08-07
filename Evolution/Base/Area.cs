using System.Collections.Generic;
using System.Drawing;

namespace Evolution.Base
{
    public enum AreaKind { Food, Water, Poison, Forest, Desert, Farm, Irrigation }

    public class Area
    {
        public AreaKind Kind { get; set; }
        public Rectangle Bounds { get; set; }

        // Set for team-claimed plots; null for the natural biomes.
        public int? OwnerTeamId { get; set; }

        // Consecutive ticks the current challenger has held this plot unopposed.
        public int CaptureProgress { get; set; }
        public int? CapturingTeamId { get; set; }

        // Food/Water/Farm/Irrigation (consumable pools)
        public List<ResourceBubble> Bubbles { get; set; } = new List<ResourceBubble>();

        // Poison/Forest/Desert (fixed features)
        public List<Rectangle> StaticBubbles { get; set; } = new List<Rectangle>();

        public bool IsFoodLike => Kind == AreaKind.Food || Kind == AreaKind.Farm;
        public bool IsWaterLike => Kind == AreaKind.Water || Kind == AreaKind.Irrigation;
    }
}

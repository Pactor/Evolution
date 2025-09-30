using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Evolution.Base
{
    public class Entity
    {
        private static readonly Random rand = new Random();

        // --- Core ---
        public int X { get; set; }
        public int Y { get; set; }
        public int Size { get; } = 8;

        // --- Team ---
        public int TeamId { get; }
        public Color TeamColor { get; }
        public int BuildCooldown { get; set; } = 0;

        // --- Needs / State / Brain ---
        public Brain Brain { get; } = new Brain();
        public Color Color { get; private set; }
        public int Hunger { get; set; } = 50;  // 0..100
        public int Thirst { get; set; } = 50;  // 0..100
        public int Health { get; set; } = 100; // 0..100
        public bool IsAlive => Health > 0;

        public int LastDrainTick { get; set; } = 0;

        public EntityState State { get; set; } = EntityState.SearchingFood;
        public ResourceBubble TargetBubble { get; set; }
        public Entity TargetMate { get; set; }
        public Entity TargetEnemy { get; set; }
        public bool InCombat => TargetEnemy != null && TargetEnemy.IsAlive;

        // --- Repro ---
        public DateTime LastReproduced { get; set; } = DateTime.MinValue;
        public bool ReadyToReproduce => Hunger >= 100 && Thirst >= 100 && Brain.Has(18);

        public Entity(int startX, int startY, int teamId, Color teamColor)
        {
            X = startX;
            Y = startY;
            TeamId = teamId;
            TeamColor = teamColor;
            Color = teamColor;

            // start with a random movement ability
            byte[] moves = { 1, 2, 3, 4 };
            Brain.AddAbility(moves[rand.Next(moves.Length)], 1);
        }

        public void Draw(Graphics g)
        {
            using (var brush = new SolidBrush(Color))
                g.FillEllipse(brush, X, Y, Size, Size);

            Pen outline = Pens.Black;
            switch (State)
            {
                case EntityState.SearchingFood: outline = Pens.Orange; break;
                case EntityState.EatingFood: outline = Pens.DarkOrange; break;
                case EntityState.SearchingWater: outline = Pens.Blue; break;
                case EntityState.DrinkingWater: outline = Pens.DarkBlue; break;
                case EntityState.SearchingMate: outline = Pens.Magenta; break;
                case EntityState.Moving: outline = Pens.Gray; break;
                case EntityState.Reproducing: outline = Pens.Red; break;
            }
            g.DrawEllipse(outline, X, Y, Size, Size);

            using (var pen = new Pen(TeamColor, 1))
                g.DrawEllipse(pen, X + 1, Y + 1, Size - 2, Size - 2);

            int barWidth = Size;
            int barHeight = 3;
            int filled = Math.Max(0, Math.Min(barWidth, (int)(barWidth * (Health / 100.0))));
            g.FillRectangle(Brushes.Red, X, Y - barHeight - 1, barWidth, barHeight);
            g.FillRectangle(Brushes.Lime, X, Y - barHeight - 1, filled, barHeight);
        }

        public void TickNeeds(int tickCount)
        {
            if (tickCount - LastDrainTick >= 800)
            {
                LastDrainTick = tickCount;
                if (Hunger > 0) Hunger--;
                if (Thirst > 0) Thirst--;
            }

            if (Hunger >= 90 && Thirst >= 90 && !InCombat && Health < 100)
            {
                Health++;
            }
        }

        // =========================
        // === MAIN TICK LOGIC   ===
        // =========================
        public void Tick(Rectangle bounds, Rectangle legendBox,
                         Area foodArea, Area waterArea,
                         List<Point> avoidZones)
        {
            if (!IsAlive) return;
            if (BuildCooldown > 0) BuildCooldown--;

            // === PRIORITY 1: Survival ===
            // --- Survival (Food/Water) ---
            if (Hunger < 100 || Thirst < 100)
            {
                // ✅ Stay locked in until full
                if (State == EntityState.EatingFood && Hunger < 100)
                    return;
                if (State == EntityState.DrinkingWater && Thirst < 100)
                    return;

                // Otherwise go find whichever need is lower
                if (Hunger <= Thirst)
                {
                    State = EntityState.SearchingFood;
                    TargetBubble = FindNearest(foodArea);
                    if (TargetBubble != null)
                        MoveToward(bounds, legendBox, TargetBubble.Bounds, avoidZones);
                }
                else
                {
                    State = EntityState.SearchingWater;
                    TargetBubble = FindNearest(waterArea);
                    if (TargetBubble != null)
                        MoveToward(bounds, legendBox, TargetBubble.Bounds, avoidZones);
                }
                return;
            }

            // === PRIORITY 2: Farming / Irrigation ===
            if (Brain.Has(31) || Brain.Has(32))
            {
                // Check team surpluses
                int teamFood = Form1.Instance.GetTeamFood(TeamId);
                int teamWater = Form1.Instance.GetTeamWater(TeamId);

                if (teamFood < 1000 || teamWater < 1000)
                {
                    State = EntityState.Moving;
                    RandomMove(bounds, legendBox, avoidZones);
                    return;
                }
                else
                {
                    // Surpluses OK → head for forest to reproduce
                    SwitchToMate();
                    var forestTarget = FindNearestForestBubble(Form1.ForestArea);
                    if (forestTarget != null)
                    {
                        MoveToward(bounds, legendBox, forestTarget.Value, avoidZones);
                        return;
                    }
                }
            }

            // === PRIORITY 3: Reproduction ===
            if (ReadyToReproduce)
            {
                State = EntityState.SearchingMate;

                // Try to find a mate nearby
                var mate = FindMateNearby(Form1.Instance.Entities, 60);
                if (mate != null)
                {
                    TargetMate = mate;
                    MoveToward(bounds, legendBox, new Rectangle(mate.X, mate.Y, mate.Size, mate.Size), avoidZones);
                    return;
                }

                // Otherwise head to forest
                var forestTarget = FindNearestForestBubble(Form1.ForestArea);
                if (forestTarget != null)
                {
                    MoveToward(bounds, legendBox, forestTarget.Value, avoidZones);
                    return;
                }
            }

            // === Fallback: Explore ===
            State = EntityState.Moving;
            RandomMove(bounds, legendBox, avoidZones);
        }

        // =========================
        // === HELPER METHODS    ===
        // =========================
        public void SwitchToMate()
        {
            if (!Brain.Has(18)) Brain.AddAbility(18, 1);
            State = EntityState.SearchingMate;
            TargetBubble = null;
        }

        private Entity FindMateNearby(List<Entity> entities, int radius = 50)
        {
            return entities.FirstOrDefault(e =>
                e != this &&
                e.TeamId == TeamId &&
                e.ReadyToReproduce &&
                Math.Abs(e.X - X) <= radius &&
                Math.Abs(e.Y - Y) <= radius);
        }

        public ResourceBubble FindNearest(Area area)
        {
            if (area?.Bubbles == null || area.Bubbles.Count == 0) return null;
            ResourceBubble best = null;
            double bestDist = double.MaxValue;
            foreach (var b in area.Bubbles)
            {
                double cx = b.Bounds.X + b.Bounds.Width / 2.0;
                double cy = b.Bounds.Y + b.Bounds.Height / 2.0;
                double dx = cx - X, dy = cy - Y, dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist)
                {
                    best = b;
                    bestDist = dist;
                }
            }
            return best;
        }

        private Rectangle? FindNearestForestBubble(Area forestArea)
        {
            if (forestArea?.StaticBubbles == null || forestArea.StaticBubbles.Count == 0) return null;

            Rectangle best = forestArea.StaticBubbles[0];
            double bestDist = double.MaxValue;

            foreach (var s in forestArea.StaticBubbles)
            {
                double cx = s.X + s.Width / 2.0;
                double cy = s.Y + s.Height / 2.0;
                double dx = cx - X, dy = cy - Y, dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist)
                {
                    best = s;
                    bestDist = dist;
                }
            }
            return best;
        }

        private void MoveToward(Rectangle bounds, Rectangle legendBox, Rectangle? target, List<Point> avoid = null)
        {
            if (target == null) { RandomMove(bounds, legendBox, avoid); return; }

            int cx = target.Value.X + target.Value.Width / 2;
            int cy = target.Value.Y + target.Value.Height / 2;

            int step = 2;
            int newX = X, newY = Y;
            if (X < cx) newX += step;
            if (X > cx) newX -= step;
            if (Y < cy) newY += step;
            if (Y > cy) newY -= step;

            Rectangle next = new Rectangle(newX, newY, Size, Size);

            if (!bounds.Contains(next) || legendBox.IntersectsWith(next))
            {
                RandomMove(bounds, legendBox, avoid);
                return;
            }

            X = newX; Y = newY;
        }

        private void RandomMove(Rectangle bounds, Rectangle legendBox, List<Point> avoid = null)
        {
            int dir = rand.Next(4);
            int step = 2;
            int newX = X, newY = Y;
            if (dir == 0) newY -= step;
            if (dir == 1) newY += step;
            if (dir == 2) newX += step;
            if (dir == 3) newX -= step;

            Rectangle next = new Rectangle(newX, newY, Size, Size);

            if (bounds.Contains(next) && !legendBox.IntersectsWith(next))
            {
                X = newX; Y = newY;
            }
        }
    }
}

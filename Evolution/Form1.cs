using Evolution.Base;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Evolution
{
    /// <summary>
    /// Renders a <see cref="World"/> and drives it on a timer. All the simulation
    /// rules live in World so they can also run headless (see Program --sim).
    /// </summary>
    public partial class Form1 : Form
    {
        private const int TickIntervalMs = 200;

        private World world;
        private Timer simTimer;
        private readonly ToolTip entityTooltip = new ToolTip();
        private readonly Rectangle legendRect = new Rectangle(10, 10, 160, 160);
        private readonly Random rand = new Random();

        public Form1()
        {
            InitializeComponent();
            pictureBox1.Width = 800;
            pictureBox1.Height = 600;
            ClientSize = new Size(pictureBox1.Right + 12, pictureBox1.Bottom + 12);
            pictureBox1.Paint += pictureBox1_Paint;
            pictureBox1.MouseMove += pictureBox1_MouseMove;
            Load += Form1_Load;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            simTimer = new Timer { Interval = TickIntervalMs };
            simTimer.Tick += SimTimer_Tick;
            ResetWorld();
        }

        private void button1_Click(object sender, EventArgs e) => ResetWorld();

        private void ResetWorld()
        {
            world = new World(pictureBox1.Width, pictureBox1.Height, legendRect, rand.Next());
            pictureBox1.Invalidate();
            simTimer.Start();   // a reset after game over has to bring the sim back to life
        }

        private void SimTimer_Tick(object sender, EventArgs e)
        {
            world.Tick();

            // Paint the final frame before any modal dialog goes up
            pictureBox1.Invalidate();
            pictureBox1.Update();

            if (world.Outcome != null)
            {
                simTimer.Stop();
                MessageBox.Show(world.Outcome.Message, world.Outcome.Title);
            }
        }

        // ======================
        // === DRAWING        ===
        // ======================
        private static Brush FillFor(AreaKind kind)
        {
            switch (kind)
            {
                case AreaKind.Food: case AreaKind.Farm: return Brushes.Gold;
                case AreaKind.Water: case AreaKind.Irrigation: return Brushes.DeepSkyBlue;
                case AreaKind.Poison: return Brushes.OliveDrab;
                case AreaKind.Forest: return Brushes.ForestGreen;
                default: return Brushes.SandyBrown;
            }
        }

        private static Pen OutlineFor(AreaKind kind)
        {
            switch (kind)
            {
                case AreaKind.Food: case AreaKind.Farm: return Pens.SaddleBrown;
                case AreaKind.Water: case AreaKind.Irrigation: return Pens.DarkBlue;
                case AreaKind.Poison: return Pens.DarkOliveGreen;
                case AreaKind.Forest: return Pens.DarkGreen;
                default: return Pens.Peru;
            }
        }

        private void pictureBox1_Paint(object sender, PaintEventArgs e)
        {
            if (world == null) return;

            var gfx = e.Graphics;
            gfx.Clear(Color.LightGray);

            foreach (var area in new[] { world.Food, world.Water, world.Poison, world.Forest, world.Desert })
                DrawArea(gfx, area);

            // Team plots: shaded rectangle plus their tiles. A plot being taken is
            // outlined in the attacker's colour, so you can watch it change hands.
            foreach (var area in world.Plots)
            {
                var teamColor = World.ColorFor(area.OwnerTeamId ?? 0);
                using (var fill = new SolidBrush(Color.FromArgb(40, teamColor)))
                using (var pen = new Pen(teamColor, 2))
                {
                    gfx.FillRectangle(fill, area.Bounds);
                    gfx.DrawRectangle(pen, area.Bounds);
                }

                if (area.CapturingTeamId.HasValue && area.CaptureProgress > 0)
                {
                    using (var pen = new Pen(World.ColorFor(area.CapturingTeamId.Value), 3)
                                     { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                        gfx.DrawRectangle(pen, Rectangle.Inflate(area.Bounds, 3, 3));

                    int width = (int)(area.Bounds.Width * (area.CaptureProgress / (double)World.CaptureTicks));
                    using (var bar = new SolidBrush(World.ColorFor(area.CapturingTeamId.Value)))
                        gfx.FillRectangle(bar, area.Bounds.X, area.Bounds.Y - 6, width, 4);
                }

                DrawArea(gfx, area);
            }

            // Each team's breeding ground, ringed in its colour
            foreach (var nest in world.Nests)
                using (var pen = new Pen(World.ColorFor(nest.Key), 2))
                    gfx.DrawEllipse(pen, Rectangle.Inflate(nest.Value, 4, 4));

            DrawLegendBox(gfx, legendRect);

            string score = $"Team 0: {world.Population(0)} ({world.PlotCount(0)} plots)   |   " +
                           $"Team 1: {world.Population(1)} ({world.PlotCount(1)} plots)";
            using (var f = new Font("Arial", 12, FontStyle.Bold))
            {
                var size = gfx.MeasureString(score, f);
                gfx.DrawString(score, f, Brushes.Black, (pictureBox1.Width - size.Width) / 2, 5);
            }

            foreach (var ent in world.Entities)
                DrawEntity(gfx, ent);
        }

        private void DrawArea(Graphics g, Area area)
        {
            var fill = FillFor(area.Kind);
            var outline = OutlineFor(area.Kind);

            foreach (var b in area.Bubbles)
            {
                g.FillEllipse(fill, b.Bounds);
                g.DrawEllipse(outline, b.Bounds);

                if (b.OwnerTeamId.HasValue)
                    using (var pen = new Pen(World.ColorFor(b.OwnerTeamId.Value), 2))
                        g.DrawEllipse(pen, Rectangle.Inflate(b.Bounds, -2, -2));
            }

            foreach (var s in area.StaticBubbles)
            {
                g.FillEllipse(fill, s);
                g.DrawEllipse(outline, s);
            }
        }

        private static void DrawEntity(Graphics g, Entity ent)
        {
            using (var brush = new SolidBrush(ent.Color))
                g.FillEllipse(brush, ent.Bounds);

            Pen outline;
            switch (ent.State)
            {
                case EntityState.SearchingFood: outline = Pens.Orange; break;
                case EntityState.EatingFood: outline = Pens.DarkOrange; break;
                case EntityState.SearchingWater: outline = Pens.Blue; break;
                case EntityState.DrinkingWater: outline = Pens.DarkBlue; break;
                case EntityState.SearchingMate: outline = Pens.Magenta; break;
                case EntityState.Fighting: outline = Pens.Red; break;
                case EntityState.Raiding: outline = Pens.DarkRed; break;
                case EntityState.Moving: outline = Pens.Gray; break;
                default: outline = Pens.Black; break;
            }
            g.DrawEllipse(outline, ent.Bounds);

            using (var pen = new Pen(ent.TeamColor, 1))
                g.DrawEllipse(pen, ent.X + 1, ent.Y + 1, ent.Size - 2, ent.Size - 2);

            const int barHeight = 3;
            int filled = Math.Max(0, Math.Min(ent.Size, (int)(ent.Size * (ent.Health / 100.0))));
            g.FillRectangle(Brushes.Red, ent.X, ent.Y - barHeight - 1, ent.Size, barHeight);
            g.FillRectangle(Brushes.Lime, ent.X, ent.Y - barHeight - 1, filled, barHeight);
        }

        private void DrawLegendBox(Graphics g, Rectangle rect)
        {
            g.FillRectangle(Brushes.WhiteSmoke, rect);
            g.DrawRectangle(Pens.Black, rect);
            int y = rect.Y + 6;
            DrawLegendItem(g, "Food", Brushes.Gold, Pens.SaddleBrown, rect.X + 6, y);
            DrawLegendItem(g, "Water", Brushes.DeepSkyBlue, Pens.DarkBlue, rect.X + 6, y + 22);
            DrawLegendItem(g, "Poison", Brushes.OliveDrab, Pens.DarkOliveGreen, rect.X + 6, y + 44);
            DrawLegendItem(g, "Forest", Brushes.ForestGreen, Pens.DarkGreen, rect.X + 6, y + 66);
            DrawLegendItem(g, "Desert", Brushes.SandyBrown, Pens.Peru, rect.X + 6, y + 88);

            for (int team = 0; team <= 1; team++)
            {
                int ty = y + 112 + team * 18;
                using (var b = new SolidBrush(World.ColorFor(team)))
                    g.FillRectangle(b, rect.X + 6, ty, 15, 12);
                g.DrawRectangle(Pens.Black, rect.X + 6, ty, 15, 12);
                g.DrawString($"Team {team}", SystemFonts.DefaultFont, Brushes.Black, rect.X + 26, ty - 2);
            }
        }

        private static void DrawLegendItem(Graphics g, string label, Brush fill, Pen outline, int x, int y)
        {
            g.FillEllipse(fill, x, y, 15, 15);
            g.DrawEllipse(outline, x, y, 15, 15);
            g.DrawString(label, SystemFonts.DefaultFont, Brushes.Black, x + 22, y - 2);
        }

        // ======================
        // === TOOLTIPS       ===
        // ======================
        private void pictureBox1_MouseMove(object sender, MouseEventArgs e)
        {
            if (world == null) return;

            string info = Describe(e.Location);
            if (info == null) { entityTooltip.Hide(pictureBox1); return; }
            entityTooltip.Show(info, pictureBox1, e.Location.X + 15, e.Location.Y + 15, 1200);
        }

        private string Describe(Point at)
        {
            foreach (var ent in world.Entities)
                if (ent.Bounds.Contains(at))
                    return $"Entity (Team {ent.TeamId})\n" +
                           $"State: {ent.State}\n" +
                           $"Hunger: {ent.Hunger}\n" +
                           $"Thirst: {ent.Thirst}\n" +
                           $"Health: {ent.Health}\n" +
                           $"Abilities: {Describe(ent.Brain)}";

            foreach (var area in new[] { world.Food, world.Water }.Concat(world.Plots))
                foreach (var b in area.Bubbles)
                    if (b.Bounds.Contains(at))
                    {
                        string owner = b.OwnerTeamId.HasValue ? $"Team {b.OwnerTeamId}" : "None";
                        return $"{area.Kind} Resource\nOwner: {owner}\nRemaining: {b.Value}";
                    }

            if (world.Forest.StaticBubbles.Any(s => s.Contains(at)))
                return "Forest\nNo resource drain\nBreeding ground";
            if (world.Desert.StaticBubbles.Any(s => s.Contains(at)))
                return "Desert\nDrains thirst faster";
            if (world.Poison.StaticBubbles.Any(s => s.Contains(at)))
                return "Poison\nThis will kill an entity";

            return null;
        }

        private static string Describe(Brain brain)
        {
            var names = new List<string>();
            for (int i = 0; i < brain.AbilityCount; i++)
            {
                switch (brain.IdAt(i))
                {
                    case Entity.AbilityReproduce: names.Add("Breed"); break;
                    case Entity.AbilityFight: names.Add("Fight"); break;
                    case Entity.AbilityNest: names.Add("Nest"); break;
                    case Entity.AbilityFarm: names.Add("Farm"); break;
                    case Entity.AbilityIrrigate: names.Add("Irrigate"); break;
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : "none";
        }
    }
}

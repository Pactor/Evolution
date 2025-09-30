using Evolution.Base;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Evolution
{
    public partial class Form1 : Form
    {
        private readonly Random rand = new Random();
        private readonly List<Entity> entities = new List<Entity>();
        private Timer simTimer;
        private ToolTip entityTooltip = new ToolTip();

        private Rectangle legendRect = new Rectangle(10, 10, 160, 160);

        // Natural biomes/resources
        private Area food, water, poison, forest, desert;

        // Team-claimed farm / irrigation areas (1 each per team)
        private readonly Dictionary<int, Area> teamFarmAreas = new Dictionary<int, Area>();
        private readonly Dictionary<int, Area> teamIrrigateAreas = new Dictionary<int, Area>();

        // Keep ALL area rectangles here to avoid overlaps (with spacing)
        private readonly List<Rectangle> globalAreaBounds = new List<Rectangle>();

        private List<Point> poisonDeaths = new List<Point>();
        private int tickCount = 0;

        // teams
        private readonly Color team0 = Color.FromArgb(70, 160, 255);
        private readonly Color team1 = Color.FromArgb(255, 120, 120);

        public static Area ForestArea { get; private set; }

        public static Form1 Instance { get; private set; }

        public List<Entity> Entities => entities;
        public Form1()
        {
            InitializeComponent();
            pictureBox1.Width = 800;
            pictureBox1.Height = 600;
            this.Width = pictureBox1.Width + 100;
            this.Height = pictureBox1.Height + 100;
            pictureBox1.Paint += pictureBox1_Paint;
            pictureBox1.MouseMove += pictureBox1_MouseMove;
            Load += Form1_Load;
            Instance = this;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            ResetWorld();
            simTimer = new Timer { Interval = 200 };
            simTimer.Tick += SimTimer_Tick;
            simTimer.Start();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            ResetWorld();
        }

        // ======================
        // === WORLD SETUP   ===
        // ======================
        private void ResetWorld()
        {
            entities.Clear();
            poisonDeaths.Clear();
            tickCount = 0;
            teamFarmAreas.Clear();
            teamIrrigateAreas.Clear();

            // Reset global bounds and reserve space around the legend
            globalAreaBounds.Clear();
            globalAreaBounds.Add(Rectangle.Inflate(legendRect, 40, 40));

            // Create non-overlapping natural areas (registered immediately)
            food = MakeResourceArea(140, 200, 100, 160, Brushes.Gold, Pens.SaddleBrown, 12, 15, 20);
            water = MakeResourceArea(140, 200, 100, 160, Brushes.DeepSkyBlue, Pens.DarkBlue, 10, 15, 20);
            poison = MakeStaticArea(120, 180, 100, 160, Brushes.OliveDrab, Pens.DarkOliveGreen, 8, 15, 20);
            forest = MakeStaticArea(160, 220, 120, 180, Brushes.ForestGreen, Pens.DarkGreen, 10, 20, 30);
            desert = MakeStaticArea(160, 220, 120, 180, Brushes.SandyBrown, Pens.Peru, 8, 20, 30);

            ForestArea = forest;

            // Spawn two teams on opposite sides
            for (int i = 0; i < 10; i++)
                entities.Add(new Entity(30, rand.Next(legendRect.Bottom + 40, pictureBox1.Height - 30), 0, team0));
            for (int i = 0; i < 10; i++)
                entities.Add(new Entity(pictureBox1.Width - 50, rand.Next(legendRect.Bottom + 40, pictureBox1.Height - 30), 1, team1));
        }

        // ======================
        // === TICK / UPDATE  ===
        // ======================
        private void SimTimer_Tick(object sender, EventArgs e)
        {
            tickCount++;
            var world = new Rectangle(0, 0, pictureBox1.Width, pictureBox1.Height);

            // Needs drain & regen
            foreach (var ent in entities.ToList())
                ent.TickNeeds(tickCount);

            // Team surpluses (include team areas too)
            int team0Food = food.Bubbles.Where(b => b.OwnerTeamId == 0).Sum(b => b.Value)
                           + (teamFarmAreas.ContainsKey(0) ? teamFarmAreas[0].Bubbles.Sum(b => b.Value) : 0);

            int team0Water = water.Bubbles.Where(b => b.OwnerTeamId == 0).Sum(b => b.Value)
                            + (teamIrrigateAreas.ContainsKey(0) ? teamIrrigateAreas[0].Bubbles.Sum(b => b.Value) : 0);

            int team1Food = food.Bubbles.Where(b => b.OwnerTeamId == 1).Sum(b => b.Value)
                           + (teamFarmAreas.ContainsKey(1) ? teamFarmAreas[1].Bubbles.Sum(b => b.Value) : 0);

            int team1Water = water.Bubbles.Where(b => b.OwnerTeamId == 1).Sum(b => b.Value)
                            + (teamIrrigateAreas.ContainsKey(1) ? teamIrrigateAreas[1].Bubbles.Sum(b => b.Value) : 0);

            // Entities act
            foreach (var ent in entities.ToList())
            {
                int tf = ent.TeamId == 0 ? team0Food : team1Food;
                int tw = ent.TeamId == 0 ? team0Water : team1Water;
                // (Entity.Tick is your version that uses needs, targets, and simple move)
                ent.Tick(world, legendRect, food, water, poisonDeaths);
            }

            // World effects
            HandlePoisonInteraction();
            HandleDesertInteraction();
            HandleForestCooling();

            // Combat anywhere
            HandleGlobalCombat(world);

            // Resource interactions
            HandleFoodInteraction();
            HandleWaterInteraction();

            // Farm / Irrigate (with team areas and 1000 cap)
            HandleBuilding();

            // Forest battles & reproduction
            HandleForestBattlesAndReproduction(world);

            // End conditions
            CheckTeamDeaths();
            CheckWin();

            pictureBox1.Invalidate();
        }

        // ======================
        // === HAZARDS        ===
        // ======================
        private void HandlePoisonInteraction()
        {
            foreach (var ent in entities.ToList())
            {
                var entRect = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                foreach (var s in poison.StaticBubbles)
                {
                    if (entRect.IntersectsWith(s))
                    {
                        poisonDeaths.Add(new Point(ent.X, ent.Y));
                        entities.Remove(ent);
                        break;
                    }
                }
            }
        }

        private void HandleDesertInteraction()
        {
            foreach (var ent in entities)
            {
                var entRect = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                foreach (var s in desert.StaticBubbles)
                {
                    if (entRect.IntersectsWith(s))
                    {
                        // Desert makes them thirsty faster (drain)
                        ent.Thirst = Math.Max(0, ent.Thirst - 1);
                        break;
                    }
                }
            }
        }

        private void HandleForestCooling()
        {
            foreach (var ent in entities)
            {
                var entRect = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                foreach (var s in forest.StaticBubbles)
                {
                    if (entRect.IntersectsWith(s))
                    {
                        // Gentle comfort effect
                        if (tickCount % 10 == 0)
                        {
                            ent.Hunger = Math.Min(100, ent.Hunger + 1);
                            ent.Thirst = Math.Min(100, ent.Thirst + 1);
                        }
                        break;
                    }
                }
            }
        }

        // ======================
        // === COMBAT         ===
        // ======================
        private void HandleGlobalCombat(Rectangle world)
        {
            // Collision-based fighting for enemies in contact
            for (int i = 0; i < entities.Count; i++)
            {
                for (int j = i + 1; j < entities.Count; j++)
                {
                    var e1 = entities[i];
                    var e2 = entities[j];
                    if (!e1.IsAlive || !e2.IsAlive) continue;
                    if (e1.TeamId == e2.TeamId) continue;

                    var r1 = new Rectangle(e1.X, e1.Y, e1.Size, e1.Size);
                    var r2 = new Rectangle(e2.X, e2.Y, e2.Size, e2.Size);
                    if (r1.IntersectsWith(r2))
                    {
                        e1.Health -= 5;
                        e2.Health -= 5;
                    }
                }
            }

            // Resource defense: defenders swarm enemies inside their owned bubbles
            foreach (var area in new[] { food, water })
            {
                foreach (var bubble in area.Bubbles.Where(b => b.OwnerTeamId.HasValue))
                {
                    var bubbleRect = bubble.Bounds;
                    int ownerTeam = bubble.OwnerTeamId.Value;

                    var enemiesInside = entities.Where(e => e.TeamId != ownerTeam && e.IsAlive &&
                                        new Rectangle(e.X, e.Y, e.Size, e.Size).IntersectsWith(bubbleRect)).ToList();

                    if (enemiesInside.Count > 0)
                    {
                        var defenders = entities.Where(d => d.TeamId == ownerTeam && d.IsAlive).ToList();
                        foreach (var defender in defenders)
                        {
                            foreach (var enemy in enemiesInside)
                            {
                                var dr = new Rectangle(defender.X, defender.Y, defender.Size, defender.Size);
                                var er = new Rectangle(enemy.X, enemy.Y, enemy.Size, enemy.Size);
                                if (dr.IntersectsWith(er))
                                {
                                    defender.Health -= 5;
                                    enemy.Health -= 5;
                                }
                            }
                        }
                    }
                }
            }

            // Also defend farm/irrigate bubbles
            foreach (var teamArea in teamFarmAreas.Values.Concat(teamIrrigateAreas.Values))
            {
                foreach (var bubble in teamArea.Bubbles.Where(b => b.OwnerTeamId.HasValue))
                {
                    var bubbleRect = bubble.Bounds;
                    int ownerTeam = bubble.OwnerTeamId.Value;

                    var enemiesInside = entities.Where(e => e.TeamId != ownerTeam && e.IsAlive &&
                                        new Rectangle(e.X, e.Y, e.Size, e.Size).IntersectsWith(bubbleRect)).ToList();

                    if (enemiesInside.Count > 0)
                    {
                        var defenders = entities.Where(d => d.TeamId == ownerTeam && d.IsAlive).ToList();
                        foreach (var defender in defenders)
                        {
                            foreach (var enemy in enemiesInside)
                            {
                                var dr = new Rectangle(defender.X, defender.Y, defender.Size, defender.Size);
                                var er = new Rectangle(enemy.X, enemy.Y, enemy.Size, enemy.Size);
                                if (dr.IntersectsWith(er))
                                {
                                    defender.Health -= 5;
                                    enemy.Health -= 5;
                                }
                            }
                        }
                    }
                }
            }

            entities.RemoveAll(e => !e.IsAlive);
        }

        // ======================
        // === EAT / DRINK    ===
        // ======================
        private void HandleFoodInteraction()
        {
            foreach (var ent in entities.ToList())
            {
                var entRect = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                foreach (var bubble in food.Bubbles.ToList())
                {
                    if (!entRect.IntersectsWith(bubble.Bounds)) continue;

                    if (bubble.OwnerTeamId.HasValue && bubble.OwnerTeamId.Value != ent.TeamId)
                    {
                        ent.Health -= 5;
                        if (ent.Health <= 0) entities.Remove(ent);
                        continue;
                    }

                    ent.State = EntityState.EatingFood;
                    if (bubble.Value > 0 && ent.Hunger < 100)
                    {
                        int eat = Math.Min(5, bubble.Value);
                        bubble.Value -= eat;
                        ent.Hunger = Math.Min(100, ent.Hunger + eat);

                        //ent.State = (ent.Hunger < 100) ? EntityState.EatingFood : EntityState.Moving;
                        ent.State = EntityState.EatingFood;
                        if (ent.Hunger >= 100)
                        {
                            ent.TargetBubble = null;
                            // push a bit away so they don't jitter
                            ent.X += rand.Next(-10, 11);
                            ent.Y += rand.Next(-10, 11);
                        }

                        if (bubble.Value <= 0)
                        {
                            food.Bubbles.Remove(bubble);
                            if (ent.Hunger < 100 && ent.Hunger <= 50)
                            {
                                ent.TargetBubble = null; // retarget next tick
                                ent.State = EntityState.Moving;
                            }
                            else
                            {
                                ent.TargetBubble = null;
                                ent.State = EntityState.Moving;
                            }
                        }
                    }
                }
            }
        }

        private void HandleWaterInteraction()
        {
            foreach (var ent in entities.ToList())
            {
                var entRect = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                foreach (var bubble in water.Bubbles.ToList())
                {
                    if (!entRect.IntersectsWith(bubble.Bounds)) continue;

                    if (bubble.OwnerTeamId.HasValue && bubble.OwnerTeamId.Value != ent.TeamId)
                    {
                        ent.Health -= 5;
                        if (ent.Health <= 0) entities.Remove(ent);
                        continue;
                    }

                    ent.State = EntityState.DrinkingWater;
                    if (bubble.Value > 0 && ent.Thirst < 100)
                    {
                        int drink = Math.Min(5, bubble.Value);
                        bubble.Value -= drink;
                        ent.Thirst = Math.Min(100, ent.Thirst + drink);

                        //ent.State = (ent.Thirst < 100) ? EntityState.DrinkingWater : EntityState.Moving;
                        ent.State = EntityState.DrinkingWater;
                        if (ent.Thirst >= 100)
                        {
                            ent.TargetBubble = null;
                            // push off bubble a bit
                            ent.X += rand.Next(-10, 11);
                            ent.Y += rand.Next(-10, 11);
                        }

                        if (bubble.Value <= 0)
                        {
                            water.Bubbles.Remove(bubble);
                            if (ent.Thirst < 100 && ent.Thirst <= 50)
                            {
                                ent.TargetBubble = null; // retarget next tick
                                ent.State = EntityState.Moving;
                            }
                            else
                            {
                                ent.TargetBubble = null;
                                ent.State = EntityState.Moving;
                            }
                        }
                    }
                }
            }
        }

        // ======================
        // === BUILD / FARM   ===
        // ======================
        private void HandleBuilding()
        {
            foreach (var ent in entities)
            {
                if (ent.BuildCooldown > 0) continue;
                if (!(ent.Brain.Has(31) || ent.Brain.Has(32))) continue;

                // Team totals now include team areas
                int tf = food.Bubbles.Where(b => b.OwnerTeamId == ent.TeamId).Sum(b => b.Value)
                       + (teamFarmAreas.ContainsKey(ent.TeamId) ? teamFarmAreas[ent.TeamId].Bubbles.Sum(b => b.Value) : 0);

                int tw = water.Bubbles.Where(b => b.OwnerTeamId == ent.TeamId).Sum(b => b.Value)
                       + (teamIrrigateAreas.ContainsKey(ent.TeamId) ? teamIrrigateAreas[ent.TeamId].Bubbles.Sum(b => b.Value) : 0);

                // Stop once team has both surpluses at 1000
                if (tf >= 1000 && tw >= 1000)
                {
                    if (!ent.Brain.Has(18)) ent.Brain.AddAbility(18, 1); // learn mating
                    if (!ent.Brain.Has(26)) ent.Brain.AddAbility(26, 1); // learn nest building
                    ent.State = EntityState.SearchingMate;
                    continue;
                }

                // === Farming (food) ===
                if (ent.Brain.Has(31) && tf < 1000)
                {
                    if (!teamFarmAreas.ContainsKey(ent.TeamId))
                    {
                        var newFarmArea = ClaimTeamArea(ent.TeamId, ent.TeamColor, "food");
                        teamFarmAreas[ent.TeamId] = newFarmArea;
                    }
                    AddBubbleToTeamArea(teamFarmAreas[ent.TeamId], ent.TeamId, "food");
                    ent.BuildCooldown = 10;
                }

                // === Irrigation (water) ===
                if (ent.Brain.Has(32) && tw < 1000)
                {
                    if (!teamIrrigateAreas.ContainsKey(ent.TeamId))
                    {
                        var newIrrigateArea = ClaimTeamArea(ent.TeamId, ent.TeamColor, "water");
                        teamIrrigateAreas[ent.TeamId] = newIrrigateArea;
                    }
                    AddBubbleToTeamArea(teamIrrigateAreas[ent.TeamId], ent.TeamId, "water");
                    ent.BuildCooldown = 10;
                }
            }
        }

        private Area ClaimTeamArea(int teamId, Color teamColor, string type)
        {
            int minW = 120, maxW = 160, minH = 120, maxH = 160;
            Rectangle rect = GetRandomRect(globalAreaBounds, minW, maxW, minH, maxH);
            globalAreaBounds.Add(rect);

            // Light team-colored shading to mark ownership
            Color light = Color.FromArgb(40, teamColor);
            Brush fill = new SolidBrush(light);
            Pen outline = new Pen(teamColor, 2);

            return new Area { Bounds = rect, Fill = fill, Outline = outline };
        }

        private void AddBubbleToTeamArea(Area area, int teamId, string type)
        {
            // Max 5 bubbles at a time in the claimed area
            if (area.Bubbles.Count >= 5) return;

            int size = rand.Next(18, 26);
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int bx = rand.Next(area.Bounds.X, area.Bounds.Right - size);
                int by = rand.Next(area.Bounds.Y, area.Bounds.Bottom - size);
                var r = new Rectangle(bx, by, size, size);

                if (!area.Bubbles.Any(b => b.Bounds.IntersectsWith(r)))
                {
                    // Each farm/irrigate bubble worth 200, owned by the team
                    area.Bubbles.Add(new ResourceBubble(r, owner: teamId, startValue: 200));
                    break;
                }
            }
        }

        // ======================
        // === FOREST / REPRO ===
        // ======================
        private void HandleForestBattlesAndReproduction(Rectangle world)
        {
            foreach (var s in forest.StaticBubbles)
            {
                var inside = entities.Where(e => new Rectangle(e.X, e.Y, e.Size, e.Size).IntersectsWith(s)).ToList();
                if (inside.Count <= 1) continue;

                var byTeam = inside.GroupBy(e => e.TeamId).ToList();
                if (byTeam.Count > 1)
                {
                    // Battle inside forest if opposing teams
                    foreach (var g in byTeam)
                    {
                        var enemies = inside.Where(e => e.TeamId != g.Key).ToList();
                        foreach (var ent in g)
                        {
                            // deal damage when overlapping with any enemy
                            foreach (var enemy in enemies)
                            {
                                var r1 = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                                var r2 = new Rectangle(enemy.X, enemy.Y, enemy.Size, enemy.Size);
                                if (r1.IntersectsWith(r2))
                                {
                                    ent.Health -= 5;
                                    enemy.Health -= 5;
                                }
                            }
                        }
                    }
                    entities.RemoveAll(e => !e.IsAlive);
                }
                else
                {
                    // One team inside forest, if they are ready, they reproduce
                    if (inside.All(e => e.ReadyToReproduce))
                    {
                        inside = inside.OrderBy(e => e.X).ToList();
                        for (int i = 0; i + 1 < inside.Count; i += 2)
                            SpawnChild(inside[i], inside[i + 1]);
                    }
                }
            }
        }

        private void SpawnChild(Entity p1, Entity p2)
        {
            if (p1 == null || p2 == null) return;
            var baby = new Entity(p1.X + 10, p1.Y + 10, p1.TeamId, p1.TeamColor);
            entities.Add(baby);
            p1.Hunger = p1.Thirst = p2.Hunger = p2.Thirst = 50;
        }

        // ======================
        // === END CONDITIONS ===
        // ======================
        private void CheckTeamDeaths()
        {
            int t0 = entities.Count(e => e.TeamId == 0);
            int t1 = entities.Count(e => e.TeamId == 1);

            if (t0 == 0 || t1 == 0)
            {
                simTimer.Stop();
                string winner = t0 > 0 ? "Team 0" : "Team 1";
                MessageBox.Show($"{winner} wins! The other team is eliminated.", "Game Over");
            }
        }

        private void CheckWin()
        {
            int t0 = entities.Count(e => e.TeamId == 0);
            int t1 = entities.Count(e => e.TeamId == 1);
            if (t0 >= 25 || t1 >= 25)
            {
                simTimer.Stop();
                string winner = t0 >= 25 ? "Team 0" : "Team 1";
                MessageBox.Show($"{winner} wins! Population reached 25.", "Victory");
            }
        }

        // ======================
        // === AREA CREATION  ===
        // ======================
        private Area MakeResourceArea(int minW, int maxW, int minH, int maxH,
                                      Brush fill, Pen outline, int bubbleCount, int minSize, int maxSize)
        {
            Rectangle rect = GetRandomRect(globalAreaBounds, minW, maxW, minH, maxH);
            globalAreaBounds.Add(rect);

            var area = new Area { Bounds = rect, Fill = fill, Outline = outline };
            for (int i = 0; i < bubbleCount; i++)
            {
                int size = rand.Next(minSize, maxSize);
                int bx = rand.Next(rect.X, rect.Right - size);
                int by = rand.Next(rect.Y, rect.Bottom - size);
                area.Bubbles.Add(new ResourceBubble(new Rectangle(bx, by, size, size)));
            }
            return area;
        }

        private Area MakeStaticArea(int minW, int maxW, int minH, int maxH,
                                    Brush fill, Pen outline, int bubbleCount, int minSize, int maxSize)
        {
            Rectangle rect = GetRandomRect(globalAreaBounds, minW, maxW, minH, maxH);
            globalAreaBounds.Add(rect);

            var area = new Area { Bounds = rect, Fill = fill, Outline = outline };
            for (int i = 0; i < bubbleCount; i++)
            {
                int size = rand.Next(minSize, maxSize);
                int bx = rand.Next(rect.X, rect.Right - size);
                int by = rand.Next(rect.Y, rect.Bottom - size);
                area.StaticBubbles.Add(new Rectangle(bx, by, size, size));
            }
            return area;
        }

        private Rectangle GetRandomRect(List<Rectangle> used, int minW, int maxW, int minH, int maxH)
        {
            int padding = 80;
            for (int tries = 0; tries < 2000; tries++)
            {
                int w = rand.Next(minW, maxW);
                int h = rand.Next(minH, maxH);
                int x = rand.Next(0, pictureBox1.Width - w);
                int y = rand.Next(legendRect.Bottom + 40, pictureBox1.Height - h);
                Rectangle rect = new Rectangle(x, y, w, h);

                bool collides = used.Any(r => Rectangle.Inflate(r, padding, padding).IntersectsWith(rect));
                if (!collides)
                    return rect;
            }

            // If we got here, spacing is too tight. Gradually reduce padding and retry.
            for (int pad = 70; pad >= 0; pad -= 10)
            {
                for (int tries = 0; tries < 2000; tries++)
                {
                    int w = rand.Next(minW, maxW);
                    int h = rand.Next(minH, maxH);
                    int x = rand.Next(0, pictureBox1.Width - w);
                    int y = rand.Next(legendRect.Bottom + 40, pictureBox1.Height - h);
                    Rectangle rect = new Rectangle(x, y, w, h);

                    bool collides = used.Any(r => Rectangle.Inflate(r, pad, pad).IntersectsWith(rect));
                    if (!collides)
                        return rect;
                }
            }

            // Last resort: stack at bottom-right, but at least offset so they don’t overlap
            int offset = used.Count * 40;
            return new Rectangle(pictureBox1.Width - minW - offset, pictureBox1.Height - minH - offset, minW, minH);
        }



        // ======================
        // === DRAWING        ===
        // ======================
        private void pictureBox1_Paint(object sender, PaintEventArgs e)
        {
            var gfx = e.Graphics;
            gfx.Clear(Color.LightGray);

            // Natural areas
            DrawArea(gfx, food);
            DrawArea(gfx, water);
            DrawArea(gfx, poison);
            DrawArea(gfx, forest);
            DrawArea(gfx, desert);

            // Team areas (shaded rect + their bubbles)
            foreach (var area in teamFarmAreas.Values)
            {
                gfx.FillRectangle(area.Fill, area.Bounds);
                gfx.DrawRectangle(area.Outline, area.Bounds);
                DrawArea(gfx, area);
            }
            foreach (var area in teamIrrigateAreas.Values)
            {
                gfx.FillRectangle(area.Fill, area.Bounds);
                gfx.DrawRectangle(area.Outline, area.Bounds);
                DrawArea(gfx, area);
            }

            // Legend
            DrawLegendBox(gfx, legendRect);

            // Scoreboard
            int t0 = entities.Count(en => en.TeamId == 0);
            int t1 = entities.Count(en => en.TeamId == 1);
            string score = $"Team 0: {t0}   |   Team 1: {t1}";
            using (var f = new Font("Arial", 12, FontStyle.Bold))
            {
                var size = gfx.MeasureString(score, f);
                gfx.DrawString(score, f, Brushes.Black,
                    (pictureBox1.Width - size.Width) / 2, 5);
            }

            // Entities
            foreach (var ent in entities)
                ent.Draw(gfx);

            // Debug overlay: show all area bounds (can comment out)
            foreach (var r in globalAreaBounds)
                gfx.DrawRectangle(Pens.LightGray, r);
        }

        private void DrawArea(Graphics g, Area area)
        {
            // Resource bubbles
            foreach (var b in area.Bubbles)
            {
                Brush bubbleFill;
                Pen bubbleOutline;

                // Color by resource type: farm areas behave like food, irrigate like water
                if (teamFarmAreas.Values.Contains(area) || area == food)
                {
                    bubbleFill = Brushes.Gold;
                    bubbleOutline = Pens.SaddleBrown;
                }
                else if (teamIrrigateAreas.Values.Contains(area) || area == water)
                {
                    bubbleFill = Brushes.DeepSkyBlue;
                    bubbleOutline = Pens.DarkBlue;
                }
                else
                {
                    bubbleFill = area.Fill;      // not used for dynamic resource bubbles, but safe
                    bubbleOutline = area.Outline;
                }

                g.FillEllipse(bubbleFill, b.Bounds);
                g.DrawEllipse(bubbleOutline, b.Bounds);

                // Owner ring
                if (b.OwnerTeamId.HasValue)
                {
                    var ring = Rectangle.Inflate(b.Bounds, -2, -2);
                    using (var pen = new Pen(b.OwnerTeamId.Value == 0 ? team0 : team1, 2))
                        g.DrawEllipse(pen, ring);
                }
            }

            // Static bubbles (poison/forest/desert)
            foreach (var s in area.StaticBubbles)
            {
                g.FillEllipse(area.Fill, s);
                g.DrawEllipse(area.Outline, s);
            }
        }

        // ======================
        // === TOOLTIPS       ===
        // ======================
        private void pictureBox1_MouseMove(object sender, MouseEventArgs e)
        {
            // Entities first
            foreach (var ent in entities)
            {
                var r = new Rectangle(ent.X, ent.Y, ent.Size, ent.Size);
                if (r.Contains(e.Location))
                {
                    string info =
                        $"Entity (Team {ent.TeamId})\n" +
                        $"State: {ent.State}\n" +
                        $"Hunger: {ent.Hunger}\n" +
                        $"Thirst: {ent.Thirst}\n" +
                        $"Health: {ent.Health}\n" +
                        $"Build CD: {ent.BuildCooldown}";
                    entityTooltip.Show(info, pictureBox1, e.Location.X + 15, e.Location.Y + 15, 1200);
                    return;
                }
            }

            // Food
            foreach (var b in food.Bubbles)
                if (b.Bounds.Contains(e.Location))
                {
                    string owner = b.OwnerTeamId.HasValue ? $"Team {b.OwnerTeamId}" : "None";
                    string info = $"Food Resource\nOwner: {owner}\nRemaining: {b.Value}";
                    entityTooltip.Show(info, pictureBox1, e.Location.X + 15, e.Location.Y + 15, 1200);
                    return;
                }

            // Water
            foreach (var b in water.Bubbles)
                if (b.Bounds.Contains(e.Location))
                {
                    string owner = b.OwnerTeamId.HasValue ? $"Team {b.OwnerTeamId}" : "None";
                    string info = $"Water Resource\nOwner: {owner}\nRemaining: {b.Value}";
                    entityTooltip.Show(info, pictureBox1, e.Location.X + 15, e.Location.Y + 15, 1200);
                    return;
                }

            // Forest
            foreach (var s in forest.StaticBubbles)
                if (s.Contains(e.Location))
                {
                    entityTooltip.Show("Forest\nNo resource drain\nAllows Entities to reproduce", pictureBox1,
                        e.Location.X + 15, e.Location.Y + 15, 1200);
                    return;
                }

            // Desert
            foreach (var s in desert.StaticBubbles)
                if (s.Contains(e.Location))
                {
                    entityTooltip.Show("Desert\nDouble resource drain", pictureBox1,
                        e.Location.X + 15, e.Location.Y + 15, 1200);
                    return;
                }

            // Poison
            foreach (var s in poison.StaticBubbles)
                if (s.Contains(e.Location))
                {
                    entityTooltip.Show("Poison\nThis will kill an entity", pictureBox1,
                        e.Location.X + 15, e.Location.Y + 15, 1200);
                    return;
                }

            // Team farm bubbles
            foreach (var area in teamFarmAreas.Values)
                foreach (var b in area.Bubbles)
                    if (b.Bounds.Contains(e.Location))
                    {
                        string info = $"Farmed Food Resource\nOwner: Team {b.OwnerTeamId}\nRemaining: {b.Value}";
                        entityTooltip.Show(info, pictureBox1, e.Location.X + 15, e.Location.Y + 15, 1200);
                        return;
                    }

            // Team irrigate bubbles
            foreach (var area in teamIrrigateAreas.Values)
                foreach (var b in area.Bubbles)
                    if (b.Bounds.Contains(e.Location))
                    {
                        string info = $"Irrigated Water Resource\nOwner: Team {b.OwnerTeamId}\nRemaining: {b.Value}";
                        entityTooltip.Show(info, pictureBox1, e.Location.X + 15, e.Location.Y + 15, 1200);
                        return;
                    }

            entityTooltip.Hide(pictureBox1);
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

            using (var b0 = new SolidBrush(team0))
            using (var b1 = new SolidBrush(team1))
            {
                g.FillRectangle(b0, rect.X + 6, y + 112, 15, 12);
                g.DrawRectangle(Pens.Black, rect.X + 6, y + 112, 15, 12);
                g.DrawString("Team 0", SystemFonts.DefaultFont, Brushes.Black, rect.X + 26, y + 110);

                g.FillRectangle(b1, rect.X + 6, y + 130, 15, 12);
                g.DrawRectangle(Pens.Black, rect.X + 6, y + 130, 15, 12);
                g.DrawString("Team 1", SystemFonts.DefaultFont, Brushes.Black, rect.X + 26, y + 128);
            }
        }

        private void DrawLegendItem(Graphics g, string label, Brush fill, Pen outline, int x, int y)
        {
            g.FillEllipse(fill, x, y, 15, 15);
            g.DrawEllipse(outline, x, y, 15, 15);
            g.DrawString(label, SystemFonts.DefaultFont, Brushes.Black, x + 22, y - 2);
        }
        public int GetTeamFood(int teamId)
        {
            int fromNatural = food.Bubbles.Where(b => b.OwnerTeamId == teamId).Sum(b => b.Value);
            int fromFarm = teamFarmAreas.ContainsKey(teamId) ? teamFarmAreas[teamId].Bubbles.Sum(b => b.Value) : 0;
            return fromNatural + fromFarm;
        }

        public int GetTeamWater(int teamId)
        {
            int fromNatural = water.Bubbles.Where(b => b.OwnerTeamId == teamId).Sum(b => b.Value);
            int fromIrrigate = teamIrrigateAreas.ContainsKey(teamId) ? teamIrrigateAreas[teamId].Bubbles.Sum(b => b.Value) : 0;
            return fromNatural + fromIrrigate;
        }
    }
}

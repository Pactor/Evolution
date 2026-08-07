using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Evolution.Base
{
    public class GameOutcome
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public int? WinningTeam { get; set; }
    }

    /// <summary>
    /// The entire simulation with no UI attached. Form1 renders it on a timer;
    /// Program's --sim mode runs it headless at full speed for tuning.
    ///
    /// The loop the whole thing is built around: eat and drink out in the world,
    /// carry that back to a forest clearing, and turn it into a tile there — a full
    /// belly plus the farming skill makes food, a slaked thirst plus irrigation
    /// makes water. Tiles feed the pair that made them so they can stay and breed,
    /// which is exactly why the other team wants them, and why they have to be held.
    /// </summary>
    public class World
    {
        // --- Tuning ---
        public const int WinPopulation = 25;
        public const int TeamSize = 10;
        public const int TilesPerPlot = 5;
        public const int TileValue = 200;
        public const int FullLarder = TilesPerPlot * TileValue;
        public const int BuildCooldownTicks = 10;
        public const int FarmReach = 45;         // how near a clearing you must be to work it
        public const int CombatDamage = 5;
        public const int RaidDamage = 10;        // extra value a fighter wrecks per tick, on top of eating
        public const int HomeGroundDefence = 3;  // damage shrugged off inside your own plot
        public const int AggroRange = 140;
        public const int CaptureTicks = 30;      // unopposed ticks needed to take a plot over
        public const int ContestRange = 190;     // close enough to "come across" an enemy plot
        public const int RaidThreshold = FullLarder / 2;
        public const int StalemateTicks = 20000;
        public const int WildTileValue = 100;    // a wild pool holds half what a farmed tile does
        public const int WildRegrowTicks = 20;
        public const int WildRegrowAmount = 40;

        public static readonly Color Team0Color = Color.FromArgb(70, 160, 255);
        public static readonly Color Team1Color = Color.FromArgb(255, 120, 120);

        private const int wildFoodCount = 12;
        private const int wildWaterCount = 10;

        private readonly Random rand;
        private readonly List<Entity> entities = new List<Entity>();
        private readonly List<Rectangle> naturalBounds = new List<Rectangle>();

        // Plots change hands, so they are keyed by nothing — ownership lives on the Area.
        private readonly List<Area> plots = new List<Area>();
        private readonly Dictionary<int, Rectangle> teamNests = new Dictionary<int, Rectangle>();

        public Rectangle Bounds { get; }
        public Rectangle Reserved { get; }
        public Area Food { get; private set; }
        public Area Water { get; private set; }
        public Area Poison { get; private set; }
        public Area Forest { get; private set; }
        public Area Desert { get; private set; }

        public IReadOnlyList<Entity> Entities => entities;
        public IEnumerable<Area> Plots => plots;
        public IReadOnlyDictionary<int, Rectangle> Nests => teamNests;

        public int TickCount { get; private set; }
        public GameOutcome Outcome { get; private set; }

        // --- Stats for the headless harness ---
        public int Births { get; private set; }
        public int PoisonDeaths { get; private set; }
        public int CombatDeaths { get; private set; }
        public int StarvationDeaths { get; private set; }
        public int TilesBuilt { get; private set; }
        public int TilesRaided { get; private set; }
        public int PlotsCaptured { get; private set; }
        public int FirstBirthTick { get; private set; } = -1;
        public int FirstTileTick { get; private set; } = -1;

        public World(int width, int height, Rectangle reserved, int seed)
        {
            Bounds = new Rectangle(0, 0, width, height);
            Reserved = reserved;
            rand = new Random(seed);
            Reset();
        }

        public static Color ColorFor(int teamId) => teamId == 0 ? Team0Color : Team1Color;

        // ======================
        // === WORLD SETUP    ===
        // ======================
        public void Reset()
        {
            entities.Clear();
            plots.Clear();
            teamNests.Clear();
            naturalBounds.Clear();

            TickCount = 0;
            Outcome = null;
            Births = PoisonDeaths = CombatDeaths = StarvationDeaths = 0;
            TilesBuilt = TilesRaided = PlotsCaptured = 0;
            FirstBirthTick = FirstTileTick = -1;

            naturalBounds.Add(Rectangle.Inflate(Reserved, 40, 40));

            Food = MakeArea(AreaKind.Food, 140, 200, 100, 160, wildFoodCount, 15, 20, consumable: true);
            Water = MakeArea(AreaKind.Water, 140, 200, 100, 160, wildWaterCount, 15, 20, consumable: true);
            Poison = MakeArea(AreaKind.Poison, 120, 180, 100, 160, 8, 15, 20, consumable: false);
            Forest = MakeArea(AreaKind.Forest, 160, 220, 120, 180, 10, 20, 30, consumable: false);
            Desert = MakeArea(AreaKind.Desert, 160, 220, 120, 180, 8, 20, 30, consumable: false);

            SpawnTeam(0, 30);
            SpawnTeam(1, Bounds.Width - 50);
            AssignNests();
        }

        private void SpawnTeam(int teamId, int startX)
        {
            var team = new List<Entity>();
            for (int i = 0; i < TeamSize; i++)
            {
                var ent = new Entity(startX, rand.Next(Reserved.Bottom + 40, Bounds.Height - 30),
                                     teamId, ColorFor(teamId), rand);
                team.Add(ent);
                entities.Add(ent);
            }

            // Farming and irrigation are deliberately NOT guaranteed. A team that never
            // learns them isn't doomed — it can fight for tiles the other team built,
            // which is a whole second way to play. Only breeding is guaranteed, since
            // without it a team cannot win by population at all.
            if (!team.Any(e => e.Brain.Has(Entity.AbilityReproduce)))
                team[rand.Next(team.Count)].Brain.AddAbility(Entity.AbilityReproduce, 1);
        }

        /// Each team starts out heading for the clearing nearest its spawn — and never
        /// the same one as the other team, or the nursery is a battlefield from tick one.
        private void AssignNests()
        {
            if (Forest.StaticBubbles.Count == 0) return;

            var centre = entities.GroupBy(e => e.TeamId).ToDictionary(
                g => g.Key, g => new PointF((float)g.Average(e => e.X), (float)g.Average(e => e.Y)));

            foreach (var kv in centre)
                teamNests[kv.Key] = NearestClearing(kv.Value, null);

            // If both teams want the same clearing the closer one keeps it and the
            // other takes its next best. Simply assigning in team order would hand
            // team 0 the better nest every game — a real thumb on the scale.
            var teams = teamNests.Keys.ToList();
            if (teams.Count == 2 && Forest.StaticBubbles.Count > 1 &&
                teamNests[teams[0]] == teamNests[teams[1]])
            {
                var contested = teamNests[teams[0]];
                double cx = contested.X + contested.Width / 2.0, cy = contested.Y + contested.Height / 2.0;

                int loser = Dist2(cx, cy, centre[teams[0]].X, centre[teams[0]].Y) <=
                            Dist2(cx, cy, centre[teams[1]].X, centre[teams[1]].Y)
                            ? teams[1] : teams[0];

                teamNests[loser] = NearestClearing(centre[loser], contested);
            }
        }

        private Rectangle NearestClearing(PointF from, Rectangle? exclude) =>
            Forest.StaticBubbles
                .Where(s => !exclude.HasValue || s != exclude.Value)
                .OrderBy(s => Dist2(s.X + s.Width / 2.0, s.Y + s.Height / 2.0, from.X, from.Y))
                .First();

        private static double Dist2(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        /// Entities are stored team 0 first. Acting and eating in list order would give
        /// team 0 first pick of every contested bubble, every tick, for the whole game.
        private List<Entity> Shuffled()
        {
            var list = entities.ToList();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                var swap = list[i]; list[i] = list[j]; list[j] = swap;
            }
            return list;
        }

        // ======================
        // === TICK           ===
        // ======================
        public void Tick()
        {
            if (Outcome != null) return;
            TickCount++;

            foreach (var ent in entities.ToList())
                ent.TickNeeds(TickCount);
            StarvationDeaths += entities.RemoveAll(e => !e.IsAlive);

            foreach (var ent in Shuffled())
                ent.Tick(this);

            RegrowWild();
            HandlePoison();
            HandleDesert();
            HandleForestComfort();
            HandleCombat();
            ConsumeResources(FoodSources().ToList(), isFood: true);
            ConsumeResources(WaterSources().ToList(), isFood: false);
            HandleFarming();
            HandleCapture();
            HandleReproduction();

            Outcome = CheckOutcome();
        }

        // Every pool in the world, including the other team's. Entity.IsEdible decides
        // which of them a given entity is actually willing (or able) to use.
        public IEnumerable<Area> FoodSources() =>
            new[] { Food }.Concat(plots.Where(p => p.Kind == AreaKind.Farm));

        public IEnumerable<Area> WaterSources() =>
            new[] { Water }.Concat(plots.Where(p => p.Kind == AreaKind.Irrigation));

        // ======================
        // === WILD REGROWTH  ===
        // ======================
        /// Wild food and water grow back slowly. Without this the map is a one-shot:
        /// the biomes hold barely enough to fill everyone once, and after that nobody
        /// can ever reach the full-bellied state that building a tile requires — you
        /// would need water to make water. The trickle sustains a population at
        /// subsistence; only tiles give the surplus that lets a team actually breed.
        private void RegrowWild()
        {
            if (TickCount % WildRegrowTicks != 0) return;
            Regrow(Food, wildFoodCount);
            Regrow(Water, wildWaterCount);
        }

        private void Regrow(Area area, int originalCount)
        {
            var lowest = area.Bubbles.OrderBy(b => b.Value).FirstOrDefault();
            if (lowest != null && lowest.Value < WildTileValue)
            {
                lowest.Value = Math.Min(WildTileValue, lowest.Value + WildRegrowAmount);
                return;
            }

            if (area.Bubbles.Count >= originalCount) return;

            int size = rand.Next(15, 20);
            for (int attempt = 0; attempt < 40; attempt++)
            {
                int bx = rand.Next(area.Bounds.X, area.Bounds.Right - size);
                int by = rand.Next(area.Bounds.Y, area.Bounds.Bottom - size);
                var r = new Rectangle(bx, by, size, size);
                if (!area.Bubbles.Any(b => b.Bounds.IntersectsWith(r)))
                {
                    area.Bubbles.Add(new ResourceBubble(r, null, WildRegrowAmount));
                    return;
                }
            }
        }

        // ======================
        // === HAZARDS        ===
        // ======================
        private void HandlePoison()
        {
            foreach (var ent in entities.ToList())
                if (Poison.StaticBubbles.Any(s => ent.Bounds.IntersectsWith(s)))
                {
                    entities.Remove(ent);
                    PoisonDeaths++;
                }
        }

        private void HandleDesert()
        {
            foreach (var ent in entities)
                if (Desert.StaticBubbles.Any(s => ent.Bounds.IntersectsWith(s)))
                    ent.Thirst = Math.Max(0, ent.Thirst - 1);
        }

        private void HandleForestComfort()
        {
            if (TickCount % 10 != 0) return;
            foreach (var ent in entities)
                if (Forest.StaticBubbles.Any(s => ent.Bounds.IntersectsWith(s)))
                {
                    ent.Hunger = Math.Min(Entity.MaxNeed, ent.Hunger + 1);
                    ent.Thirst = Math.Min(Entity.MaxNeed, ent.Thirst + 1);
                }
        }

        // ======================
        // === COMBAT         ===
        // ======================
        // One proximity pass covers open ground, contested plots and clearings alike.
        // Reach extends past the body so two entities can't stride through each other.
        private void HandleCombat()
        {
            int before = entities.Count;

            // Tally every blow first, then apply. Deducting health as we go would let
            // an entity that dies early in the loop stop hitting back — and because
            // team 0 sits at the low indices, that quietly favoured team 0 all game.
            var damage = new Dictionary<Entity, int>();
            for (int i = 0; i < entities.Count; i++)
            {
                for (int j = i + 1; j < entities.Count; j++)
                {
                    var a = entities[i];
                    var b = entities[j];
                    if (a.TeamId == b.TeamId) continue;
                    if (!a.CanStrike(b)) continue;

                    damage[a] = (damage.ContainsKey(a) ? damage[a] : 0) + DamageTaken(a);
                    damage[b] = (damage.ContainsKey(b) ? damage[b] : 0) + DamageTaken(b);
                }
            }

            foreach (var hit in damage)
                hit.Key.Health -= hit.Value;

            entities.RemoveAll(e => !e.IsAlive);
            CombatDeaths += before - entities.Count;
        }

        /// Fighting on your own plot hurts less — that is what makes a plot holdable.
        private int DamageTaken(Entity target)
        {
            bool home = OwnedPlots(target.TeamId).Any(a => a.Bounds.Contains(target.Bounds));
            return home ? Math.Max(1, CombatDamage - HomeGroundDefence) : CombatDamage;
        }

        private IEnumerable<Area> OwnedPlots(int teamId) => plots.Where(a => a.OwnerTeamId == teamId);

        // ======================
        // === EAT / DRINK    ===
        // ======================
        private void ConsumeResources(List<Area> areas, bool isFood)
        {
            foreach (var ent in Shuffled())
            {
                foreach (var area in areas)
                {
                    foreach (var bubble in area.Bubbles.ToList())
                    {
                        if (!ent.Bounds.IntersectsWith(bubble.Bounds)) continue;

                        // Enemy stores: a fighter helps itself and wrecks the rest, anyone
                        // else just gets hurt trying. Being able to live off enemy land is
                        // what finally makes the fighting trait worth inheriting.
                        bool enemyTile = bubble.OwnerTeamId.HasValue && bubble.OwnerTeamId.Value != ent.TeamId;
                        if (enemyTile && !ent.Brain.Has(Entity.AbilityFight))
                        {
                            ent.Health -= CombatDamage;
                            if (!ent.IsAlive) { entities.Remove(ent); CombatDeaths++; break; }
                            continue;
                        }

                        int need = isFood ? ent.Hunger : ent.Thirst;
                        if (bubble.Value <= 0 || need >= Entity.MaxNeed) continue;

                        int taken = Math.Min(5, Math.Min(bubble.Value, Entity.MaxNeed - need));
                        bubble.Value -= taken;
                        if (isFood) ent.Hunger += taken; else ent.Thirst += taken;
                        ent.State = isFood ? EntityState.EatingFood : EntityState.DrinkingWater;

                        // Pillage: what a raider can't eat, it ruins.
                        if (enemyTile)
                        {
                            bubble.Value -= RaidDamage;
                            TilesRaided++;
                            ent.State = EntityState.Raiding;
                        }

                        bool full = (isFood ? ent.Hunger : ent.Thirst) >= Entity.MaxNeed;
                        bool drained = bubble.Value <= 0;
                        if (drained) area.Bubbles.Remove(bubble);

                        if (full || drained)
                        {
                            // Drop the eating state along with the meal — a stale state
                            // would otherwise freeze the entity in place forever.
                            ent.TargetBubble = null;
                            ent.State = EntityState.Moving;
                        }
                    }

                    if (!ent.IsAlive) break;
                }
            }
        }

        // ======================
        // === FARMING        ===
        // ======================
        /// The heart of it: an entity that carried a full belly to a clearing turns that
        /// into a food tile, and one that arrives with its thirst slaked turns that into
        /// water. Nothing is handed out at spawn — every tile on the map was walked there.
        private void HandleFarming()
        {
            foreach (var ent in Shuffled())
            {
                if (ent.BuildCooldown > 0) continue;

                var clearing = ClearingUnder(ent);
                if (clearing == null) continue;

                bool madeSomething = false;

                if (ent.Hunger >= Entity.SatedThreshold && ent.Brain.Has(Entity.AbilityFarm))
                    madeSomething |= BuildTile(ent, clearing.Value, AreaKind.Farm);

                if (ent.Thirst >= Entity.SatedThreshold && ent.Brain.Has(Entity.AbilityIrrigate))
                    madeSomething |= BuildTile(ent, clearing.Value, AreaKind.Irrigation);

                if (madeSomething)
                {
                    ent.BuildCooldown = BuildCooldownTicks;
                    ent.State = EntityState.Farming;
                }
            }
        }

        /// The forest clearing an entity is close enough to work, if any.
        private Rectangle? ClearingUnder(Entity ent)
        {
            foreach (var s in Forest.StaticBubbles)
                if (ent.Bounds.IntersectsWith(Rectangle.Inflate(s, FarmReach, FarmReach)))
                    return s;
            return null;
        }

        private bool BuildTile(Entity ent, Rectangle clearing, AreaKind kind)
        {
            var plot = plots
                .Where(p => p.Kind == kind && p.OwnerTeamId == ent.TeamId)
                .OrderBy(p => Dist2(p.Bounds.X + p.Bounds.Width / 2.0,
                                    p.Bounds.Y + p.Bounds.Height / 2.0, ent.CenterX, ent.CenterY))
                .FirstOrDefault();

            if (plot == null)
            {
                plot = new Area { Kind = kind, OwnerTeamId = ent.TeamId, Bounds = FindPlotNear(clearing) };
                plots.Add(plot);
                naturalBounds.Add(plot.Bounds);
            }

            if (plot.Bubbles.Count >= TilesPerPlot) return false;

            int size = rand.Next(18, 26);
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int bx = rand.Next(plot.Bounds.X, plot.Bounds.Right - size);
                int by = rand.Next(plot.Bounds.Y, plot.Bounds.Bottom - size);
                var r = new Rectangle(bx, by, size, size);

                if (!plot.Bubbles.Any(b => b.Bounds.IntersectsWith(r)))
                {
                    plot.Bubbles.Add(new ResourceBubble(r, owner: ent.TeamId, startValue: TileValue));
                    TilesBuilt++;
                    if (FirstTileTick < 0) FirstTileTick = TickCount;
                    return true;
                }
            }
            return false;
        }

        /// Plots hug the clearing they were worked from, so a breeding pair can eat,
        /// drink and mate without crossing the map. The forest itself is not an
        /// obstacle — farming at the clearing's edge is the whole point.
        private Rectangle FindPlotNear(Rectangle clearing)
        {
            const int w = 110, h = 110;
            double cx = clearing.X + clearing.Width / 2.0, cy = clearing.Y + clearing.Height / 2.0;

            var blockers = new List<Rectangle>
            {
                Rectangle.Inflate(Reserved, 20, 20),
                Food.Bounds, Water.Bounds, Poison.Bounds, Desert.Bounds
            };
            blockers.AddRange(plots.Select(p => p.Bounds));

            for (int radius = 55; radius <= 420; radius += 10)
            {
                for (int attempt = 0; attempt < 60; attempt++)
                {
                    double angle = rand.NextDouble() * Math.PI * 2;
                    int x = (int)(cx + Math.Cos(angle) * radius) - w / 2;
                    int y = (int)(cy + Math.Sin(angle) * radius) - h / 2;
                    var rect = new Rectangle(x, y, w, h);

                    if (!Bounds.Contains(rect)) continue;
                    if (blockers.Any(o => o.IntersectsWith(rect))) continue;
                    return rect;
                }
            }

            return GetRandomRect(naturalBounds, w, w + 1, h, h + 1);
        }

        // ======================
        // === TERRITORY      ===
        // ======================
        /// Hold a plot unopposed for long enough and it becomes yours, tiles and all.
        /// While both teams have somebody standing on it, nobody makes progress —
        /// they have to win the fight first.
        private void HandleCapture()
        {
            foreach (var plot in plots)
            {
                var inside = entities.Where(e => e.Bounds.IntersectsWith(plot.Bounds)).ToList();
                var teamsPresent = inside.Select(e => e.TeamId).Distinct().ToList();

                if (teamsPresent.Count != 1)
                {
                    plot.CaptureProgress = 0;
                    plot.CapturingTeamId = null;
                    continue;
                }

                int claimant = teamsPresent[0];
                if (claimant == plot.OwnerTeamId)
                {
                    plot.CaptureProgress = 0;
                    plot.CapturingTeamId = null;
                    continue;
                }

                if (plot.CapturingTeamId != claimant)
                {
                    plot.CapturingTeamId = claimant;
                    plot.CaptureProgress = 0;
                }

                if (++plot.CaptureProgress < CaptureTicks) continue;

                plot.OwnerTeamId = claimant;
                foreach (var b in plot.Bubbles) b.OwnerTeamId = claimant;
                plot.CaptureProgress = 0;
                plot.CapturingTeamId = null;
                PlotsCaptured++;
            }
        }

        // ======================
        // === REPRODUCTION   ===
        // ======================
        /// Breeding happens in any forest clearing, not just the one a team started
        /// beside — so a team that takes ground can raise young on it. Nobody breeds
        /// in a clearing the enemy is standing in.
        private void HandleReproduction()
        {
            foreach (var clearing in Forest.StaticBubbles)
            {
                var here = Rectangle.Inflate(clearing, 6, 6);
                var inside = entities.Where(e => e.Bounds.IntersectsWith(here)).ToList();
                if (inside.Count < 2) continue;

                foreach (var team in inside.Select(e => e.TeamId).Distinct().ToList())
                {
                    if (inside.Any(e => e.TeamId != team)) continue;   // contested

                    var ready = inside.Where(e => e.TeamId == team && e.ReadyToReproduce)
                                      .OrderBy(e => e.X).ToList();
                    for (int i = 0; i + 1 < ready.Count; i += 2)
                        SpawnChild(ready[i], ready[i + 1]);
                }
            }
        }

        private void SpawnChild(Entity p1, Entity p2)
        {
            // The child inherits both parents' brains with a chance of mutation —
            // this is what actually makes the population evolve over generations.
            entities.Add(Entity.CreateChild(p1, p2, rand));
            Births++;
            if (FirstBirthTick < 0) FirstBirthTick = TickCount;

            // Raising the child costs both parents, which doubles as a breeding cooldown.
            p1.Hunger = p1.Thirst = p2.Hunger = p2.Thirst = 50;
        }

        // ======================
        // === END CONDITIONS ===
        // ======================
        private GameOutcome CheckOutcome()
        {
            int t0 = entities.Count(e => e.TeamId == 0);
            int t1 = entities.Count(e => e.TeamId == 1);

            if (t0 == 0 && t1 == 0)
                return new GameOutcome { Title = "Game Over", Message = "Both teams were wiped out. Nobody wins." };

            if (t0 == 0 || t1 == 0)
            {
                int winner = t0 > 0 ? 0 : 1;
                return new GameOutcome
                {
                    Title = "Game Over",
                    WinningTeam = winner,
                    Message = $"Team {winner} wins! The other team is eliminated."
                };
            }

            if (t0 >= WinPopulation || t1 >= WinPopulation)
            {
                if (t0 == t1)
                    return new GameOutcome
                    {
                        Title = "Victory",
                        Message = $"Both teams reached {WinPopulation} at once. Honours even."
                    };

                int winner = t0 > t1 ? 0 : 1;
                return new GameOutcome
                {
                    Title = "Victory",
                    WinningTeam = winner,
                    Message = $"Team {winner} wins! Population reached {WinPopulation}."
                };
            }

            if (TickCount >= StalemateTicks)
                return new GameOutcome
                {
                    Title = "Stalemate",
                    Message = $"Neither team could finish. Team 0: {t0}, Team 1: {t1}."
                };

            return null;
        }

        // ======================
        // === QUERIES        ===
        // ======================
        public int Population(int teamId) => entities.Count(e => e.TeamId == teamId);

        public double AbilityShare(byte id) =>
            entities.Count == 0 ? 0 : entities.Count(e => e.Brain.Has(id)) / (double)entities.Count;

        public int PlotCount(int teamId) => plots.Count(p => p.OwnerTeamId == teamId);

        public Rectangle? GetNest(int teamId)
        {
            Rectangle r; return teamNests.TryGetValue(teamId, out r) ? r : (Rectangle?)null;
        }

        public int GetTeamFood(int teamId) =>
            plots.Where(p => p.Kind == AreaKind.Farm && p.OwnerTeamId == teamId)
                 .Sum(p => p.Bubbles.Sum(b => b.Value));

        public int GetTeamWater(int teamId) =>
            plots.Where(p => p.Kind == AreaKind.Irrigation && p.OwnerTeamId == teamId)
                 .Sum(p => p.Bubbles.Sum(b => b.Value));

        public Entity NearestEnemy(Entity self, int range)
        {
            Entity best = null;
            double bestDist = range * (double)range;
            foreach (var e in entities)
            {
                if (e.TeamId == self.TeamId || !e.IsAlive) continue;
                double d = Dist2(e.CenterX, e.CenterY, self.CenterX, self.CenterY);
                if (d < bestDist) { best = e; bestDist = d; }
            }
            return best;
        }

        /// Nearest enemy standing on ground this team holds. Answering intruders is
        /// worth dying for; chasing every enemy on the map is not, and a fighter that
        /// does the latter never lives long enough to pass the trait on.
        public Entity NearestIntruder(Entity self, int range)
        {
            var home = OwnedPlots(self.TeamId).Select(a => a.Bounds).ToList();
            var nest = GetNest(self.TeamId);
            if (nest.HasValue) home.Add(Rectangle.Inflate(nest.Value, 30, 30));
            if (home.Count == 0) return null;

            Entity best = null;
            double bestDist = range * (double)range;
            foreach (var e in entities)
            {
                if (e.TeamId == self.TeamId || !e.IsAlive) continue;
                if (!home.Any(h => h.IntersectsWith(e.Bounds))) continue;

                double d = Dist2(e.CenterX, e.CenterY, self.CenterX, self.CenterY);
                if (d < bestDist) { best = e; bestDist = d; }
            }
            return best;
        }

        /// The nearest plot belonging to somebody else — the thing worth taking.
        public Rectangle? NearestEnemyPlot(Entity self)
        {
            Rectangle? best = null;
            double bestDist = double.MaxValue;

            foreach (var plot in plots)
            {
                if (plot.OwnerTeamId == self.TeamId) continue;
                double d = Dist2(plot.Bounds.X + plot.Bounds.Width / 2.0,
                                 plot.Bounds.Y + plot.Bounds.Height / 2.0, self.CenterX, self.CenterY);
                if (d < bestDist) { best = plot.Bounds; bestDist = d; }
            }
            return best;
        }

        // ======================
        // === AREA CREATION  ===
        // ======================
        private Area MakeArea(AreaKind kind, int minW, int maxW, int minH, int maxH,
                              int bubbleCount, int minSize, int maxSize, bool consumable)
        {
            Rectangle rect = GetRandomRect(naturalBounds, minW, maxW, minH, maxH);
            naturalBounds.Add(rect);

            var area = new Area { Kind = kind, Bounds = rect };
            for (int i = 0; i < bubbleCount; i++)
            {
                int size = rand.Next(minSize, maxSize);
                int bx = rand.Next(rect.X, rect.Right - size);
                int by = rand.Next(rect.Y, rect.Bottom - size);
                var r = new Rectangle(bx, by, size, size);

                if (consumable) area.Bubbles.Add(new ResourceBubble(r));
                else area.StaticBubbles.Add(r);
            }
            return area;
        }

        private Rectangle GetRandomRect(List<Rectangle> used, int minW, int maxW, int minH, int maxH)
        {
            for (int pad = 80; pad >= 0; pad -= 10)
            {
                for (int tries = 0; tries < 2000; tries++)
                {
                    int w = rand.Next(minW, maxW);
                    int h = rand.Next(minH, maxH);
                    int x = rand.Next(0, Bounds.Width - w);
                    int y = rand.Next(Reserved.Bottom + 40, Bounds.Height - h);
                    var rect = new Rectangle(x, y, w, h);

                    if (!used.Any(r => Rectangle.Inflate(r, pad, pad).IntersectsWith(rect)))
                        return rect;
                }
            }

            int offset = used.Count * 40;
            return new Rectangle(Math.Max(0, Bounds.Width - minW - offset),
                                 Math.Max(0, Bounds.Height - minH - offset), minW, minH);
        }
    }
}

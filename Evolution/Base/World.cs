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

    /// <summary>A lineage. Teams 0 and 1 are the founders; later ones are hybrid-born.</summary>
    public class Team
    {
        public int Id { get; set; }
        public Color Color { get; set; }
        public Rectangle? Nest { get; set; }

        /// Founded by outcasts, and it shows: fights everyone, breeds on far less.
        public bool Zealot { get; set; }

        public string Name => Zealot ? $"Team {Id} (hybrid)" : $"Team {Id}";
    }

    /// <summary>
    /// The entire simulation with no UI attached. Form1 renders it on a timer;
    /// Program's --sim mode runs it headless at full speed for tuning.
    ///
    /// The loop it is built around: eat and drink out in the world, carry that back to
    /// a forest clearing, and turn it into a tile there — a full belly plus the farming
    /// skill makes food, a slaked thirst plus irrigation makes water. Tiles feed the
    /// pair that made them so they can stay and breed, which is why the other team
    /// wants them and why they have to be held.
    ///
    /// And occasionally something else happens. Two creatures from opposing teams that
    /// can both reason, both ready to breed, meeting in a clearing, don't fight — they
    /// interbreed. The child belongs to neither side and is tolerated by both, except
    /// by anyone with a mean streak. Get two such outcasts together and they found a
    /// lineage of their own, which arrives hostile to everybody.
    /// </summary>
    public class World
    {
        // --- Tuning ---
        public const int Unaffiliated = -1;      // a hybrid's "team" until it founds one
        public const int MaxTeams = 4;
        public const int WinPopulation = 25;
        public const int TeamSize = 10;
        public const int TilesPerPlot = 5;
        public const int TileValue = 200;
        public const int FullLarder = TilesPerPlot * TileValue;
        public const int BuildCooldownTicks = 10;
        public const int FarmReach = 45;
        public const int CombatDamage = 5;
        public const int RaidDamage = 10;
        public const int HomeGroundDefence = 3;
        public const int AggroRange = 140;
        public const int CaptureTicks = 30;
        public const int ContestRange = 190;
        public const int RaidThreshold = FullLarder / 2;
        public const int StalemateTicks = 20000;
        public const int WildTileValue = 100;
        public const int WildRegrowTicks = 20;
        public const int WildRegrowAmount = 40;
        public const int ForestCount = 3;
        public const int MaxClearings = 26;      // ceiling so the map doesn't become all trees
        public const int MinClearingSpacing = 85;
        public const int PlantCooldownTicks = 60;

        public static readonly Color Team0Color = Color.FromArgb(70, 160, 255);
        public static readonly Color Team1Color = Color.FromArgb(255, 120, 120);

        private const int wildFoodCount = 12;
        private const int wildWaterCount = 10;

        private readonly Random rand;
        private readonly List<Entity> entities = new List<Entity>();
        private readonly List<Rectangle> naturalBounds = new List<Rectangle>();
        private readonly List<Area> plots = new List<Area>();
        private readonly List<Area> forests = new List<Area>();
        private readonly List<Area> poisons = new List<Area>();
        private readonly List<Team> teams = new List<Team>();
        private Rectangle? commons;   // where reasoning creatures meet across team lines

        public Rectangle Bounds { get; }
        public Rectangle Reserved { get; }
        public Area Food { get; private set; }
        public Area Water { get; private set; }
        public Area Desert { get; private set; }

        public IReadOnlyList<Area> Forests => forests;
        public IReadOnlyList<Area> Poisons => poisons;
        public IEnumerable<Rectangle> Clearings => forests.SelectMany(f => f.StaticBubbles);
        public IEnumerable<Rectangle> PoisonPatches => poisons.SelectMany(p => p.StaticBubbles);
        public IReadOnlyList<Entity> Entities => entities;
        public IEnumerable<Area> Plots => plots;
        public IReadOnlyList<Team> Teams => teams;

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
        public int Interbreedings { get; private set; }
        public int LineagesFounded { get; private set; }
        public int ClearingsPlanted { get; private set; }
        public int FirstBirthTick { get; private set; } = -1;
        public int FirstTileTick { get; private set; } = -1;
        public int FirstHybridTick { get; private set; } = -1;

        public World(int width, int height, Rectangle reserved, int seed)
        {
            Bounds = new Rectangle(0, 0, width, height);
            Reserved = reserved;
            rand = new Random(seed);
            Reset();
        }

        // ======================
        // === WORLD SETUP    ===
        // ======================
        public void Reset()
        {
            entities.Clear();
            plots.Clear();
            forests.Clear();
            poisons.Clear();
            teams.Clear();
            naturalBounds.Clear();

            TickCount = 0;
            Outcome = null;
            Births = PoisonDeaths = CombatDeaths = StarvationDeaths = 0;
            TilesBuilt = TilesRaided = PlotsCaptured = 0;
            Interbreedings = LineagesFounded = ClearingsPlanted = 0;
            FirstBirthTick = FirstTileTick = FirstHybridTick = -1;
            commons = null;

            naturalBounds.Add(Rectangle.Inflate(Reserved, 40, 40));

            Food = MakeArea(AreaKind.Food, 140, 200, 100, 160, wildFoodCount, 15, 20, consumable: true);
            Water = MakeArea(AreaKind.Water, 140, 200, 100, 160, wildWaterCount, 15, 20, consumable: true);

            // One patch per half of the map. A single randomly-placed patch only ever
            // threatened whichever team happened to spawn near it, so only that side
            // was under any pressure to evolve a nose for the stuff.
            poisons.Add(MakeArea(AreaKind.Poison, 110, 160, 90, 140, 4, 15, 20, false, 0, Bounds.Width / 2));
            poisons.Add(MakeArea(AreaKind.Poison, 110, 160, 90, 140, 4, 15, 20, false,
                                 Bounds.Width / 2, Bounds.Width));

            // Several stands of forest rather than one: every lineage needs a clearing
            // of its own, and the outcasts need one that nobody has claimed.
            for (int i = 0; i < ForestCount; i++)
                forests.Add(MakeArea(AreaKind.Forest, 120, 170, 100, 150, 5, 20, 30, consumable: false));

            Desert = MakeArea(AreaKind.Desert, 160, 220, 120, 180, 8, 20, 30, consumable: false);

            teams.Add(new Team { Id = 0, Color = Team0Color });
            teams.Add(new Team { Id = 1, Color = Team1Color });

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
            // learns them isn't doomed — it can fight for tiles the other team built.
            // Only breeding is guaranteed, since without it a team cannot win at all.
            if (!team.Any(e => e.Brain.Has(Entity.AbilityReproduce)))
                team[rand.Next(team.Count)].Brain.AddAbility(Entity.AbilityReproduce, 1);
        }

        /// The two founding teams take the clearing nearest their spawn, and never the
        /// same one — the closer team keeps a contested clearing, since assigning in
        /// team order would hand team 0 the better nest every game.
        private void AssignNests()
        {
            var all = Clearings.ToList();
            if (all.Count == 0) return;

            var centre = entities.GroupBy(e => e.TeamId).ToDictionary(
                g => g.Key, g => new PointF((float)g.Average(e => e.X), (float)g.Average(e => e.Y)));

            foreach (var kv in centre)
                TeamOf(kv.Key).Nest = NearestClearing(kv.Value, null);

            if (teams.Count == 2 && all.Count > 1 && teams[0].Nest == teams[1].Nest)
            {
                var contested = teams[0].Nest.Value;
                double cx = contested.X + contested.Width / 2.0, cy = contested.Y + contested.Height / 2.0;

                int loser = Dist2(cx, cy, centre[0].X, centre[0].Y) <= Dist2(cx, cy, centre[1].X, centre[1].Y)
                            ? 1 : 0;
                TeamOf(loser).Nest = NearestClearing(centre[loser], contested);
            }

            // The commons is settled now, once, while both nests are known.
            commons = PickClearing();
        }

        private Rectangle NearestClearing(PointF from, Rectangle? exclude) =>
            Clearings
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
            HandlePlanting();
            HandleCapture();
            HandleReproduction();

            Outcome = CheckOutcome();
        }

        public IEnumerable<Area> FoodSources() =>
            new[] { Food }.Concat(plots.Where(p => p.Kind == AreaKind.Farm));

        public IEnumerable<Area> WaterSources() =>
            new[] { Water }.Concat(plots.Where(p => p.Kind == AreaKind.Irrigation));

        // ======================
        // === WILD REGROWTH  ===
        // ======================
        /// Wild pools grow back at a trickle. Without this the map is a one-shot: the
        /// biomes hold barely enough to fill everyone once, and after that nobody can
        /// reach the full state that building a tile requires — you'd need water to
        /// make water. The trickle sustains subsistence; only tiles give a surplus.
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
            var patches = PoisonPatches.ToList();
            foreach (var ent in entities.ToList())
                if (patches.Any(s => ent.Bounds.IntersectsWith(s)))
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
            var clearings = Clearings.ToList();
            foreach (var ent in entities)
            {
                if (!clearings.Any(s => ent.Bounds.IntersectsWith(s))) continue;

                ent.CarriesSeed = true;   // you leave the trees carrying seed
                if (TickCount % 10 != 0) continue;
                ent.Hunger = Math.Min(Entity.MaxNeed, ent.Hunger + 1);
                ent.Thirst = Math.Min(Entity.MaxNeed, ent.Thirst + 1);
            }
        }

        // ======================
        // === COMBAT         ===
        // ======================
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
                    if (!a.CanStrike(b)) continue;
                    if (!Hostile(a, b)) continue;

                    damage[a] = (damage.ContainsKey(a) ? damage[a] : 0) + Blow(b, a);
                    damage[b] = (damage.ContainsKey(b) ? damage[b] : 0) + Blow(a, b);
                }
            }

            foreach (var hit in damage)
                hit.Key.Health -= hit.Value;

            entities.RemoveAll(e => !e.IsAlive);
            CombatDeaths += before - entities.Count;
        }

        /// Who actually comes to blows.
        private bool Hostile(Entity a, Entity b)
        {
            if (a.TeamId == b.TeamId) return false;

            // Two outcasts have no quarrel — finding each other is the whole point.
            if (a.IsHybrid && b.IsHybrid) return false;

            // A hybrid is tolerated by both sides, for the most part. Not by everyone:
            // an aggressive streak with no reason behind it will still go for one. A
            // creature that can reason lets it be — which matters, because the commons
            // is full of reasoners and it's where the outcasts are born.
            if (a.IsHybrid) return Intolerant(b);
            if (b.IsHybrid) return Intolerant(a);

            // Reason is, above all, the capacity not to fight. Two creatures that both
            // have it keep the peace while they share a clearing — which is what lets
            // them linger on neutral ground long enough to both come into season.
            // Without this they trade blows the moment either one isn't ready, and
            // pairs kill each other the tick after they've bred.
            if (a.Brain.Has(Entity.AbilityReason) && b.Brain.Has(Entity.AbilityReason) &&
                SharedClearing(a, b) != null) return false;

            return true;
        }

        private static bool Intolerant(Entity e) =>
            e.Brain.Has(Entity.AbilityAggressive) && !e.Brain.Has(Entity.AbilityReason);

        /// The one circumstance in which opposing teams don't fight: two creatures that
        /// can both reason, both ready to breed, standing in the same forest clearing.
        private bool WillInterbreed(Entity a, Entity b) =>
            teams.Count < MaxTeams &&
            a.Brain.Has(Entity.AbilityReason) && b.Brain.Has(Entity.AbilityReason) &&
            a.ReadyToReproduce && b.ReadyToReproduce &&
            SharedClearing(a, b) != null;

        private Rectangle? SharedClearing(Entity a, Entity b)
        {
            foreach (var c in Clearings)
            {
                var here = Rectangle.Inflate(c, 6, 6);
                if (a.Bounds.IntersectsWith(here) && b.Bounds.IntersectsWith(here)) return c;
            }
            return null;
        }

        /// What one lands on the other. The attacker's build tells, and standing on your
        /// own plot takes the sting out of it — that is what makes a plot holdable.
        private int Blow(Entity attacker, Entity target)
        {
            int damage = CombatDamage + attacker.Strength;
            if (OwnedPlots(target.TeamId).Any(a => a.Bounds.Contains(target.Bounds)))
                damage -= HomeGroundDefence;
            return Math.Max(1, damage);
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

                        // Someone else's stores: a fighter helps itself and wrecks the
                        // rest, a hybrid is simply tolerated, anyone else gets hurt.
                        bool enemyTile = bubble.OwnerTeamId.HasValue &&
                                         bubble.OwnerTeamId.Value != ent.TeamId && !ent.IsHybrid;
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
        /// An entity that carried a full belly to a clearing turns that into a food
        /// tile, and one that arrives with its thirst slaked turns it into water.
        /// Nothing is handed out at spawn — every tile was walked there.
        private void HandleFarming()
        {
            foreach (var ent in Shuffled())
            {
                if (ent.BuildCooldown > 0 || ent.IsHybrid) continue;

                var clearing = ClearingUnder(ent);
                if (clearing == null) continue;

                bool made = false;
                if (ent.Hunger >= Entity.SatedThreshold && ent.Brain.Has(Entity.AbilityFarm))
                    made |= BuildTile(ent, clearing.Value, AreaKind.Farm);
                if (ent.Thirst >= Entity.SatedThreshold && ent.Brain.Has(Entity.AbilityIrrigate))
                    made |= BuildTile(ent, clearing.Value, AreaKind.Irrigation);

                if (made)
                {
                    ent.BuildCooldown = ent.BuildInterval;
                    ent.State = EntityState.Farming;
                }
            }
        }

        // ======================
        // === PLANTING       ===
        // ======================
        /// Standing in a clearing leaves an entity carrying seed. Anyone who knows how
        /// to build a nest will plant it once they've wandered far enough from the
        /// trees it came from — so forest spreads across the map on the backs of the
        /// creatures using it, and clearings stop being the hard bottleneck they were.
        private void HandlePlanting()
        {
            if (Clearings.Count() >= MaxClearings) return;

            foreach (var ent in Shuffled())
            {
                if (!ent.CarriesSeed || ent.PlantCooldown > 0) continue;
                if (!ent.Brain.Has(Entity.AbilityNest)) continue;

                int size = rand.Next(20, 28);
                var spot = new Rectangle((int)ent.CenterX - size / 2, (int)ent.CenterY - size / 2, size, size);

                if (!Bounds.Contains(spot)) continue;
                if (Reserved.IntersectsWith(spot)) continue;
                if (PoisonPatches.Any(p => p.IntersectsWith(spot))) continue;
                if (Desert.StaticBubbles.Any(d => d.IntersectsWith(spot))) continue;
                if (plots.Any(p => p.Bounds.IntersectsWith(spot))) continue;

                // Far enough from established trees to count as spreading, not thickening
                double spacing = MinClearingSpacing * (double)MinClearingSpacing;
                if (Clearings.Any(c => Dist2(c.X + c.Width / 2.0, c.Y + c.Height / 2.0,
                                             ent.CenterX, ent.CenterY) < spacing)) continue;

                forests[rand.Next(forests.Count)].StaticBubbles.Add(spot);
                ent.CarriesSeed = false;
                ent.PlantCooldown = PlantCooldownTicks;
                ClearingsPlanted++;
                if (Clearings.Count() >= MaxClearings) return;
            }
        }

        private Rectangle? ClearingUnder(Entity ent)
        {
            foreach (var s in Clearings)
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

        private Rectangle FindPlotNear(Rectangle clearing)
        {
            const int w = 110, h = 110;
            double cx = clearing.X + clearing.Width / 2.0, cy = clearing.Y + clearing.Height / 2.0;

            var blockers = new List<Rectangle>
            {
                Rectangle.Inflate(Reserved, 20, 20),
                Food.Bounds, Water.Bounds, Desert.Bounds
            };
            blockers.AddRange(poisons.Select(p => p.Bounds));
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
        /// Hold an enemy plot unopposed for long enough and it becomes yours, tiles and
        /// all. Progress resets while both sides have somebody standing on it, so the
        /// plot has to be won in a fight first. Hybrids are guests, not claimants.
        private void HandleCapture()
        {
            foreach (var plot in plots)
            {
                var inside = entities.Where(e => !e.IsHybrid && e.Bounds.IntersectsWith(plot.Bounds)).ToList();
                var present = inside.Select(e => e.TeamId).Distinct().ToList();

                if (present.Count != 1 || present[0] == plot.OwnerTeamId)
                {
                    plot.CaptureProgress = 0;
                    plot.CapturingTeamId = null;
                    continue;
                }

                if (plot.CapturingTeamId != present[0])
                {
                    plot.CapturingTeamId = present[0];
                    plot.CaptureProgress = 0;
                }

                if (++plot.CaptureProgress < CaptureTicks) continue;

                plot.OwnerTeamId = present[0];
                foreach (var b in plot.Bubbles) b.OwnerTeamId = present[0];
                plot.CaptureProgress = 0;
                plot.CapturingTeamId = null;
                PlotsCaptured++;
            }
        }

        // ======================
        // === REPRODUCTION   ===
        // ======================
        private void HandleReproduction()
        {
            foreach (var clearing in Clearings.ToList())
            {
                var here = Rectangle.Inflate(clearing, 6, 6);
                var inside = entities.Where(e => e.Bounds.IntersectsWith(here)).ToList();
                if (inside.Count < 2) continue;

                FoundLineage(inside, clearing);
                Interbreed(inside);
                BreedWithinTeams(inside);
            }
        }

        /// Two outcasts, both ready: they stop being outcasts. Everyone of their kind
        /// standing in the clearing joins them, and the new lineage arrives hostile to
        /// everybody and desperate to multiply.
        private void FoundLineage(List<Entity> inside, Rectangle clearing)
        {
            if (teams.Count >= MaxTeams) return;

            var ready = inside.Where(e => e.IsHybrid && e.ReadyToReproduce).OrderBy(e => e.X).ToList();
            if (ready.Count < 2) return;

            Entity p1 = ready[0], p2 = ready[1];
            var colour = Blend(p1.TeamColor, p2.TeamColor);
            var lineage = new Team { Id = teams.Count, Color = colour, Zealot = true, Nest = clearing };
            teams.Add(lineage);

            foreach (var outcast in inside.Where(e => e.IsHybrid).ToList())
                outcast.JoinTeam(lineage.Id, colour, true);

            var child = Entity.CreateChild(p1, p2, rand, lineage.Id, colour);
            child.JoinTeam(lineage.Id, colour, true);
            entities.Add(child);

            Births++;
            LineagesFounded++;
            if (FirstBirthTick < 0) FirstBirthTick = TickCount;
            Recover(p1);
            Recover(p2);
        }

        /// A pairing across team lines. The child belongs to neither parent's side.
        private void Interbreed(List<Entity> inside)
        {
            if (teams.Count >= MaxTeams) return;

            for (int i = 0; i < inside.Count; i++)
            {
                for (int j = i + 1; j < inside.Count; j++)
                {
                    Entity a = inside[i], b = inside[j];
                    if (a.IsHybrid || b.IsHybrid || a.TeamId == b.TeamId) continue;
                    if (!WillInterbreed(a, b)) continue;

                    var child = Entity.CreateChild(a, b, rand, Unaffiliated,
                                                   Blend(a.TeamColor, b.TeamColor));
                    entities.Add(child);

                    Births++;
                    Interbreedings++;
                    if (FirstHybridTick < 0) FirstHybridTick = TickCount;
                    if (FirstBirthTick < 0) FirstBirthTick = TickCount;
                    Recover(a);
                    Recover(b);
                    return;   // one crossing per clearing per tick
                }
            }
        }

        private void BreedWithinTeams(List<Entity> inside)
        {
            foreach (var team in inside.Where(e => !e.IsHybrid).Select(e => e.TeamId).Distinct().ToList())
            {
                // Nobody raises young while the other side is standing there — unless
                // they're the hybrid-born, who stop at nothing. Outcasts don't count as
                // a threat, since both sides tolerate them.
                bool contested = inside.Any(e => !e.IsHybrid && e.TeamId != team);
                if (contested && !IsZealotTeam(team)) continue;

                var ready = inside.Where(e => e.TeamId == team && e.ReadyToReproduce)
                                  .OrderBy(e => e.X).ToList();
                for (int i = 0; i + 1 < ready.Count; i += 2)
                    SpawnChild(ready[i], ready[i + 1]);
            }
        }

        private void SpawnChild(Entity p1, Entity p2)
        {
            var child = Entity.CreateChild(p1, p2, rand, p1.TeamId, p1.TeamColor);
            if (IsZealotTeam(p1.TeamId)) child.JoinTeam(p1.TeamId, p1.TeamColor, true);
            entities.Add(child);

            Births++;
            if (FirstBirthTick < 0) FirstBirthTick = TickCount;

            // Raising the child costs both parents, which doubles as a breeding cooldown.
            Recover(p1);
            Recover(p2);
        }

        /// What a parent is left with after raising young — hardier stock bounces back
        /// sooner, so it is back in season while the founding teams are still foraging.
        private static void Recover(Entity parent)
        {
            parent.Hunger = parent.Thirst = parent.BreedRecovery;
        }

        private static Color Blend(Color a, Color b) =>
            Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);

        // ======================
        // === END CONDITIONS ===
        // ======================
        private GameOutcome CheckOutcome()
        {
            var alive = teams.Where(t => Population(t.Id) > 0).ToList();

            if (alive.Count == 0)
                return new GameOutcome { Title = "Game Over", Message = "Every lineage died out. Nobody wins." };

            if (alive.Count == 1)
                return new GameOutcome
                {
                    Title = "Game Over",
                    WinningTeam = alive[0].Id,
                    Message = $"{alive[0].Name} wins! Every other lineage is gone."
                };

            var champions = alive.Where(t => Population(t.Id) >= WinPopulation)
                                 .OrderByDescending(t => Population(t.Id)).ToList();
            if (champions.Count > 0)
            {
                if (champions.Count > 1 && Population(champions[0].Id) == Population(champions[1].Id))
                    return new GameOutcome
                    {
                        Title = "Victory",
                        Message = $"Two lineages reached {WinPopulation} at once. Honours even."
                    };

                return new GameOutcome
                {
                    Title = "Victory",
                    WinningTeam = champions[0].Id,
                    Message = $"{champions[0].Name} wins! Population reached {WinPopulation}."
                };
            }

            if (TickCount >= StalemateTicks)
                return new GameOutcome
                {
                    Title = "Stalemate",
                    Message = "Neither lineage could finish. " +
                              string.Join(", ", alive.Select(t => $"{t.Name}: {Population(t.Id)}"))
                };

            return null;
        }

        // ======================
        // === QUERIES        ===
        // ======================
        public int Population(int teamId) => entities.Count(e => e.TeamId == teamId);
        public int HybridCount => entities.Count(e => e.IsHybrid);

        public Team TeamOf(int teamId) => teams.FirstOrDefault(t => t.Id == teamId);

        public bool IsZealotTeam(int teamId)
        {
            var team = TeamOf(teamId);
            return team != null && team.Zealot;
        }

        public Color ColorFor(int teamId)
        {
            var team = TeamOf(teamId);
            return team != null ? team.Color : Color.Gray;
        }

        public double AbilityShare(byte id) =>
            entities.Count == 0 ? 0 : entities.Count(e => e.Brain.Has(id)) / (double)entities.Count;

        public int PlotCount(int teamId) => plots.Count(p => p.OwnerTeamId == teamId);

        public Rectangle? GetNest(int teamId)
        {
            var team = TeamOf(teamId);
            return team != null ? team.Nest : null;
        }

        /// The commons: the clearing furthest from anybody's nest. Outcasts gather here
        /// to find each other, and so do creatures that can reason — which is the only
        /// reason the two sides are ever in the same clearing at the same time.
        /// Cached once chosen: forest keeps spreading, and a meeting place that moved
        /// every time somebody planted a tree would be no meeting place at all.
        /// Fixed at world creation, never recomputed. It used to be worked out lazily on
        /// first use, which meant the renderer asking for it early gave a different
        /// answer than the headless run — the same seed played out two different ways.
        /// A query that quietly mutates the simulation is a query that breaks replay.
        public Rectangle? NeutralClearing() => commons;

        private Rectangle? PickClearing()
        {
            var nests = teams.Where(t => t.Nest.HasValue).Select(t => t.Nest.Value).ToList();
            var free = Clearings.Where(c => !nests.Contains(c)).ToList();
            if (free.Count == 0) free = Clearings.ToList();
            if (free.Count == 0) return null;
            if (nests.Count == 0) return free[0];

            return free.OrderBy(c => nests.Max(n => Dist2(
                c.X + c.Width / 2.0, c.Y + c.Height / 2.0,
                n.X + n.Width / 2.0, n.Y + n.Height / 2.0))).First();
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
                if (!e.IsAlive || !Hostile(self, e)) continue;
                double d = Dist2(e.CenterX, e.CenterY, self.CenterX, self.CenterY);
                if (d < bestDist) { best = e; bestDist = d; }
            }
            return best;
        }

        /// Nearest hostile standing on ground this team holds. Answering intruders is
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
                if (!e.IsAlive || !Hostile(self, e)) continue;
                if (!home.Any(h => h.IntersectsWith(e.Bounds))) continue;

                double d = Dist2(e.CenterX, e.CenterY, self.CenterX, self.CenterY);
                if (d < bestDist) { best = e; bestDist = d; }
            }
            return best;
        }

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
                              int bubbleCount, int minSize, int maxSize, bool consumable,
                              int xMin = 0, int xMax = int.MaxValue)
        {
            Rectangle rect = GetRandomRect(naturalBounds, minW, maxW, minH, maxH, xMin, xMax);
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

        /// The legend sits in the top-left and is already in `used`, so areas may now
        /// use the full height instead of being crammed below it — which they have to,
        /// with three stands of forest to place.
        /// <param name="xMin">Left edge of the band the area must sit in — used to keep
        /// one poison patch on each half of the map.</param>
        private Rectangle GetRandomRect(List<Rectangle> used, int minW, int maxW, int minH, int maxH,
                                        int xMin = 0, int xMax = int.MaxValue)
        {
            for (int pad = 60; pad >= 0; pad -= 10)
            {
                for (int tries = 0; tries < 2000; tries++)
                {
                    int w = rand.Next(minW, maxW);
                    int h = rand.Next(minH, maxH);
                    int right = Math.Min(xMax, Bounds.Width) - w;
                    int x = rand.Next(xMin, Math.Max(xMin + 1, right));
                    int y = rand.Next(0, Bounds.Height - h);
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

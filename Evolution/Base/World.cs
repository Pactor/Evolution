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
    /// </summary>
    public class World
    {
        // --- Tuning ---
        public const int SurplusTarget = 1000;   // stockpile a team needs before it breeds
        public const int WinPopulation = 25;
        public const int TeamSize = 10;
        public const int BuildCooldownTicks = 10;
        public const int BubblesPerPlot = 5;
        public const int PlotBubbleValue = 200;  // 5 x 200 = exactly SurplusTarget
        public const int CombatDamage = 5;
        public const int RaidDamage = 10;        // extra value a fighter wrecks per tick, on top of eating
        // Stores cap at exactly SurplusTarget, so "below target" is true the moment
        // anyone takes a bite. Raiding needs a threshold that means real hardship.
        public const int RaidThreshold = SurplusTarget / 2;
        public const int HomeGroundDefence = 3;  // damage shrugged off inside your own plot
        public const int AggroRange = 140;       // how far a fighter will notice an enemy
        public const int StalemateTicks = 20000; // give up rather than run forever

        public static readonly Color Team0Color = Color.FromArgb(70, 160, 255);
        public static readonly Color Team1Color = Color.FromArgb(255, 120, 120);

        private readonly Random rand;
        private readonly List<Entity> entities = new List<Entity>();
        private readonly List<Rectangle> naturalBounds = new List<Rectangle>();

        private readonly Dictionary<int, Area> teamFarmAreas = new Dictionary<int, Area>();
        private readonly Dictionary<int, Area> teamIrrigateAreas = new Dictionary<int, Area>();
        private readonly Dictionary<int, Rectangle> teamNests = new Dictionary<int, Rectangle>();

        public Rectangle Bounds { get; }
        public Rectangle Reserved { get; }        // legend box — nothing may enter it
        public Area Food { get; private set; }
        public Area Water { get; private set; }
        public Area Poison { get; private set; }
        public Area Forest { get; private set; }
        public Area Desert { get; private set; }

        public IReadOnlyList<Entity> Entities => entities;
        public IEnumerable<Area> FarmAreas => teamFarmAreas.Values;
        public IEnumerable<Area> IrrigationAreas => teamIrrigateAreas.Values;
        public IReadOnlyDictionary<int, Rectangle> Nests => teamNests;

        public int TickCount { get; private set; }
        public GameOutcome Outcome { get; private set; }

        // --- Stats, for the headless harness ---
        public int Births { get; private set; }
        public int PoisonDeaths { get; private set; }
        public int CombatDeaths { get; private set; }
        public int TilesRaided { get; private set; }
        public int FirstBirthTick { get; private set; } = -1;
        public Dictionary<int, int> SurplusTick { get; } = new Dictionary<int, int>();

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
            teamFarmAreas.Clear();
            teamIrrigateAreas.Clear();
            teamNests.Clear();
            naturalBounds.Clear();
            SurplusTick.Clear();

            TickCount = 0;
            Outcome = null;
            Births = PoisonDeaths = CombatDeaths = TilesRaided = 0;
            FirstBirthTick = -1;

            naturalBounds.Add(Rectangle.Inflate(Reserved, 40, 40));

            Food = MakeArea(AreaKind.Food, 140, 200, 100, 160, 12, 15, 20, consumable: true);
            Water = MakeArea(AreaKind.Water, 140, 200, 100, 160, 10, 15, 20, consumable: true);
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

            // A team missing either trade could never reach its surplus, so guarantee one of each.
            if (!team.Any(e => e.Brain.Has(Entity.AbilityFarm)))
                team[rand.Next(team.Count)].Brain.AddAbility(Entity.AbilityFarm, 1);
            if (!team.Any(e => e.Brain.Has(Entity.AbilityIrrigate)))
                team[rand.Next(team.Count)].Brain.AddAbility(Entity.AbilityIrrigate, 1);
            if (!team.Any(e => e.Brain.Has(Entity.AbilityFight)))
                team[rand.Next(team.Count)].Brain.AddAbility(Entity.AbilityFight, 1);
        }

        /// Each team breeds in the forest clearing nearest its spawn — and never the
        /// same clearing as the other team, or the nursery becomes a battlefield.
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

            foreach (var ent in Shuffled())
                ent.Tick(this);

            HandlePoison();
            HandleDesert();
            HandleForestComfort();
            HandleCombat();
            ConsumeResources(FoodSources().ToList(), isFood: true);
            ConsumeResources(WaterSources().ToList(), isFood: false);
            HandleBuilding();
            HandleReproduction();

            Outcome = CheckOutcome();
        }

        // Every pool in the world, including the other team's. Entity.IsEdible decides
        // which of them a given entity is actually willing (or able) to use.
        public IEnumerable<Area> FoodSources() => new[] { Food }.Concat(teamFarmAreas.Values);
        public IEnumerable<Area> WaterSources() => new[] { Water }.Concat(teamIrrigateAreas.Values);

        // ======================
        // === HAZARDS        ===
        // ======================
        private void HandlePoison()
        {
            foreach (var ent in entities.ToList())
            {
                if (Poison.StaticBubbles.Any(s => ent.Bounds.IntersectsWith(s)))
                {
                    entities.Remove(ent);
                    PoisonDeaths++;
                }
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
        // One proximity pass covers open ground, contested plots and the forest alike.
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

        /// Fighting on your own plot hurts less — that is what makes a plot worth holding.
        private int DamageTaken(Entity target)
        {
            bool home = OwnedPlots(target.TeamId).Any(a => a.Bounds.Contains(target.Bounds));
            return home ? Math.Max(1, CombatDamage - HomeGroundDefence) : CombatDamage;
        }

        private IEnumerable<Area> OwnedPlots(int teamId) =>
            teamFarmAreas.Values.Concat(teamIrrigateAreas.Values).Where(a => a.OwnerTeamId == teamId);

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
        // === BUILD / FARM   ===
        // ======================
        private void HandleBuilding()
        {
            // Random team order: whoever claims a plot first gets the pick closest to
            // its nest, because later plots have to dodge the ones already standing.
            // Always letting team 0 go first is worth a real head start.
            var teamOrder = entities.Select(e => e.TeamId).Distinct().OrderBy(t => rand.Next()).ToList();

            foreach (var teamId in teamOrder)
            {
                int tf = GetTeamFood(teamId);
                int tw = GetTeamWater(teamId);

                if (tf >= SurplusTarget && tw >= SurplusTarget && !SurplusTick.ContainsKey(teamId))
                    SurplusTick[teamId] = TickCount;

                bool surplus = tf >= SurplusTarget && tw >= SurplusTarget;

                foreach (var ent in entities.Where(e => e.TeamId == teamId))
                {
                    // The stockpile feeds the whole team, so everyone learns to breed —
                    // not only the members who happened to be born farmers.
                    if (surplus)
                    {
                        if (!ent.Brain.Has(Entity.AbilityReproduce)) ent.Brain.AddAbility(Entity.AbilityReproduce, 1);
                        if (!ent.Brain.Has(Entity.AbilityNest)) ent.Brain.AddAbility(Entity.AbilityNest, 1);
                        continue;
                    }

                    if (ent.BuildCooldown > 0) continue;

                    if (ent.Brain.Has(Entity.AbilityFarm) && tf < SurplusTarget)
                    {
                        if (!teamFarmAreas.ContainsKey(teamId))
                            teamFarmAreas[teamId] = ClaimPlot(teamId, AreaKind.Farm);
                        AddBubbleToPlot(teamFarmAreas[teamId], teamId);
                        ent.BuildCooldown = BuildCooldownTicks;
                        ent.State = EntityState.Farming;
                    }

                    if (ent.Brain.Has(Entity.AbilityIrrigate) && tw < SurplusTarget)
                    {
                        if (!teamIrrigateAreas.ContainsKey(teamId))
                            teamIrrigateAreas[teamId] = ClaimPlot(teamId, AreaKind.Irrigation);
                        AddBubbleToPlot(teamIrrigateAreas[teamId], teamId);
                        ent.BuildCooldown = BuildCooldownTicks;
                        ent.State = EntityState.Irrigating;
                    }
                }
            }
        }

        private Area ClaimPlot(int teamId, AreaKind kind)
        {
            var nest = teamNests.ContainsKey(teamId)
                ? teamNests[teamId]
                : new Rectangle(Bounds.Width / 2, Bounds.Height / 2, 20, 20);

            var area = new Area { Kind = kind, OwnerTeamId = teamId, Bounds = FindPlotNear(nest) };
            naturalBounds.Add(area.Bounds);
            return area;
        }

        /// Plots hug the nest so a breeding pair can eat, drink and mate without
        /// crossing the map. The forest itself is not an obstacle — farming at the
        /// edge of the clearing is the whole point of learning to farm.
        private Rectangle FindPlotNear(Rectangle nest)
        {
            const int w = 110, h = 110;
            double cx = nest.X + nest.Width / 2.0, cy = nest.Y + nest.Height / 2.0;

            var blockers = new List<Rectangle> { Rectangle.Inflate(Reserved, 20, 20) };
            blockers.Add(Food.Bounds);
            blockers.Add(Water.Bounds);
            blockers.Add(Poison.Bounds);
            blockers.Add(Desert.Bounds);
            blockers.AddRange(teamFarmAreas.Values.Select(a => a.Bounds));
            blockers.AddRange(teamIrrigateAreas.Values.Select(a => a.Bounds));

            for (int radius = 60; radius <= 420; radius += 10)
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

            // Nothing fits nearby: settle for anywhere legal.
            return GetRandomRect(naturalBounds, w, w + 1, h, h + 1);
        }

        private void AddBubbleToPlot(Area area, int teamId)
        {
            if (area.Bubbles.Count >= BubblesPerPlot) return;

            int size = rand.Next(18, 26);
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int bx = rand.Next(area.Bounds.X, area.Bounds.Right - size);
                int by = rand.Next(area.Bounds.Y, area.Bounds.Bottom - size);
                var r = new Rectangle(bx, by, size, size);

                if (!area.Bubbles.Any(b => b.Bounds.IntersectsWith(r)))
                {
                    area.Bubbles.Add(new ResourceBubble(r, owner: teamId, startValue: PlotBubbleValue));
                    return;
                }
            }
        }

        // ======================
        // === REPRODUCTION   ===
        // ======================
        private void HandleReproduction()
        {
            foreach (var pair in teamNests)
            {
                var clearing = Rectangle.Inflate(pair.Value, 6, 6);
                var ready = entities
                    .Where(e => e.TeamId == pair.Key && e.ReadyToReproduce && e.Bounds.IntersectsWith(clearing))
                    .OrderBy(e => e.X)
                    .ToList();

                for (int i = 0; i + 1 < ready.Count; i += 2)
                    SpawnChild(ready[i], ready[i + 1]);
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

            // Two survivors can circle each other forever. Call it rather than run
            // the window until somebody closes it.
            if (TickCount >= StalemateTicks)
                return new GameOutcome
                {
                    Title = "Stalemate",
                    Message = $"Neither team could finish. Team 0: {t0}, Team 1: {t1}."
                };

            return null;
        }

        /// Share of the living population carrying an ability — how selection is going.
        public double AbilityShare(byte id) =>
            entities.Count == 0 ? 0 : entities.Count(e => e.Brain.Has(id)) / (double)entities.Count;

        // ======================
        // === QUERIES        ===
        // ======================
        public int Population(int teamId) => entities.Count(e => e.TeamId == teamId);

        public Area GetTeamFarmArea(int teamId)
        {
            Area a; return teamFarmAreas.TryGetValue(teamId, out a) ? a : null;
        }

        public Area GetTeamIrrigateArea(int teamId)
        {
            Area a; return teamIrrigateAreas.TryGetValue(teamId, out a) ? a : null;
        }

        public Rectangle? GetNest(int teamId)
        {
            Rectangle r; return teamNests.TryGetValue(teamId, out r) ? r : (Rectangle?)null;
        }

        public int GetTeamFood(int teamId) =>
            Food.Bubbles.Where(b => b.OwnerTeamId == teamId).Sum(b => b.Value) +
            (teamFarmAreas.ContainsKey(teamId) ? teamFarmAreas[teamId].Bubbles.Sum(b => b.Value) : 0);

        public int GetTeamWater(int teamId) =>
            Water.Bubbles.Where(b => b.OwnerTeamId == teamId).Sum(b => b.Value) +
            (teamIrrigateAreas.ContainsKey(teamId) ? teamIrrigateAreas[teamId].Bubbles.Sum(b => b.Value) : 0);

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

        /// The nearest tile belonging to somebody else — the thing worth contesting.
        public Rectangle? NearestEnemyStore(Entity self)
        {
            Rectangle? best = null;
            double bestDist = double.MaxValue;

            foreach (var area in teamFarmAreas.Values.Concat(teamIrrigateAreas.Values))
            {
                if (area.OwnerTeamId == self.TeamId) continue;
                foreach (var b in area.Bubbles)
                {
                    double d = Dist2(b.Bounds.X + b.Bounds.Width / 2.0,
                                     b.Bounds.Y + b.Bounds.Height / 2.0, self.CenterX, self.CenterY);
                    if (d < bestDist) { best = b.Bounds; bestDist = d; }
                }
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

            // Last resort: stack toward the bottom-right, offset so they don't overlap.
            int offset = used.Count * 40;
            return new Rectangle(Math.Max(0, Bounds.Width - minW - offset),
                                 Math.Max(0, Bounds.Height - minH - offset), minW, minH);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Evolution.Base
{
    public class Entity
    {
        // --- Tuning ---
        public const int MaxNeed = 100;
        // Above this an entity stops hunting resources and is free to farm, fight or mate.
        public const int SatedThreshold = 80;
        // Ticks between hunger/thirst each losing a point (the UI timer runs at 200ms).
        private const int DrainIntervalTicks = 20;
        private const int StarvationDamage = 1;   // per tick once a need bottoms out
        private const int Step = 2;
        // Reach beyond the body. Without it two entities moving Step px per tick can
        // stride straight past each other without ever registering a hit.
        public const int AttackRange = 10;

        // Odds a founder is born already knowing a trade or how to fight. Nothing here
        // is guaranteed: a team short of farmers has to take its tiles off someone else.
        private const double FounderTradeChance = 0.4;
        private const double FounderFightChance = 0.5;
        private const double FounderBreedChance = 0.8;
        // Deliberately a coin flip: entities born without a nose for poison walk into
        // it and die, so selection should push this share up over the generations.
        private const double FounderSenseChance = 0.5;
        // Odds a child keeps any single ability from a parent.
        private const double InheritChance = 0.85;
        // Odds a child picks up an ability neither parent had.
        private const double MutationChance = 0.15;

        // Ability ids from BrainSkills.txt
        public const byte AbilitySensePoison = 13;
        public const byte AbilityReproduce = 18;
        public const byte AbilityFight = 21;
        public const byte AbilityNest = 26;
        public const byte AbilityFarm = 31;
        public const byte AbilityIrrigate = 32;

        private static readonly byte[] MoveAbilities = { 1, 2, 3, 4 };
        private static readonly byte[] MutableAbilities =
        {
            1, 2, 3, 4, AbilitySensePoison, AbilityReproduce,
            AbilityFight, AbilityNest, AbilityFarm, AbilityIrrigate
        };

        private readonly Random rand;

        // --- Core ---
        public int X { get; set; }
        public int Y { get; set; }
        public int Size { get; } = 8;
        public Rectangle Bounds => new Rectangle(X, Y, Size, Size);
        public double CenterX => X + Size / 2.0;
        public double CenterY => Y + Size / 2.0;

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

        public bool ReadyToReproduce =>
            Hunger >= SatedThreshold && Thirst >= SatedThreshold && Brain.Has(AbilityReproduce);

        public Entity(int startX, int startY, int teamId, Color teamColor, Random rand)
            : this(startX, startY, teamId, teamColor, rand, null) { }

        /// <param name="inherited">Brain to copy; null rolls a fresh founder brain.</param>
        public Entity(int startX, int startY, int teamId, Color teamColor, Random rand, Brain inherited)
        {
            this.rand = rand;
            X = startX;
            Y = startY;
            TeamId = teamId;
            TeamColor = teamColor;
            Color = teamColor;

            if (inherited == null)
            {
                // Founders get one movement ability and may already know a trade.
                Brain.AddAbility(MoveAbilities[rand.Next(MoveAbilities.Length)], 1);
                if (rand.NextDouble() < FounderTradeChance) Brain.AddAbility(AbilityFarm, 1);
                if (rand.NextDouble() < FounderTradeChance) Brain.AddAbility(AbilityIrrigate, 1);
                if (rand.NextDouble() < FounderFightChance) Brain.AddAbility(AbilityFight, 1);
                if (rand.NextDouble() < FounderSenseChance) Brain.AddAbility(AbilitySensePoison, 1);
                if (rand.NextDouble() < FounderBreedChance) Brain.AddAbility(AbilityReproduce, 1);
            }
            else
            {
                for (int i = 0; i < inherited.AbilityCount; i++)
                    Brain.AddAbility(inherited.IdAt(i), inherited.LevelAt(i));
            }
        }

        /// <summary>
        /// Crossover plus mutation: the child keeps most of what each parent knew,
        /// and now and then picks up something neither of them had.
        /// </summary>
        public static Entity CreateChild(Entity p1, Entity p2, Random rand)
        {
            var childBrain = new Brain();
            InheritInto(childBrain, p1.Brain, rand);
            InheritInto(childBrain, p2.Brain, rand);

            if (rand.NextDouble() < MutationChance)
                childBrain.AddAbility(MutableAbilities[rand.Next(MutableAbilities.Length)], 1);

            // A child that inherited no way to move would be stranded for life.
            if (!MoveAbilities.Any(m => childBrain.Has(m)))
                childBrain.AddAbility(MoveAbilities[rand.Next(MoveAbilities.Length)], 1);

            return new Entity(p1.X + 10, p1.Y + 10, p1.TeamId, p1.TeamColor, rand, childBrain);
        }

        private static void InheritInto(Brain child, Brain parent, Random rand)
        {
            for (int i = 0; i < parent.AbilityCount; i++)
                if (rand.NextDouble() < InheritChance)
                    child.AddAbility(parent.IdAt(i), parent.LevelAt(i));
        }

        public double DistanceTo(Rectangle target)
        {
            double dx = target.X + target.Width / 2.0 - CenterX;
            double dy = target.Y + target.Height / 2.0 - CenterY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// Can this entity land a blow on the other? Uses reach, not body overlap.
        public bool CanStrike(Entity other)
        {
            double dx = CenterX - other.CenterX, dy = CenterY - other.CenterY;
            double reach = Size + AttackRange;
            return dx * dx + dy * dy <= reach * reach;
        }

        public void TickNeeds(int tickCount)
        {
            if (tickCount - LastDrainTick >= DrainIntervalTicks)
            {
                LastDrainTick = tickCount;
                if (Hunger > 0) Hunger--;
                if (Thirst > 0) Thirst--;
            }

            // Starving or parched costs health. Without this an entity with nothing left
            // to eat simply wanders forever and the round never resolves.
            if (Hunger == 0 || Thirst == 0)
                Health -= StarvationDamage;
            else if (Hunger >= SatedThreshold && Thirst >= SatedThreshold && !InCombat && Health < 100)
                Health++;
        }

        // =========================
        // === MAIN TICK LOGIC   ===
        // =========================
        public void Tick(World world)
        {
            if (!IsAlive) return;
            if (BuildCooldown > 0) BuildCooldown--;

            // === PRIORITY 1: Fill up ===
            // Everything downstream needs a full belly: tiles are built out of one.
            if (Hunger < SatedThreshold || Thirst < SatedThreshold)
            {
                // Every pool in the world; IsEdible filters to the ones this entity may
                // use — its own team's larders, plus enemy stores if it can fight.
                var foodSources = world.FoodSources().ToArray();
                var waterSources = world.WaterSources().ToArray();

                // Stay locked in until full — but only while actually standing on the
                // resource. Without that check a stale state freezes the entity forever.
                if (State == EntityState.EatingFood && Hunger < MaxNeed && IsTouching(foodSources))
                    return;
                if (State == EntityState.DrinkingWater && Thirst < MaxNeed && IsTouching(waterSources))
                    return;

                bool wantsFood = Hunger <= Thirst;
                State = wantsFood ? EntityState.SearchingFood : EntityState.SearchingWater;
                TargetBubble = FindNearest(wantsFood ? foodSources : waterSources);

                if (TargetBubble != null) MoveToward(world, TargetBubble.Bounds);
                else RandomMove(world);   // nothing left in reach — keep looking

                return;
            }

            // === PRIORITY 2: Defend and contest ===
            TargetEnemy = null;
            if (Brain.Has(AbilityFight))
            {
                // Hit back at whoever is already on top of us, and run down intruders
                // on our own ground — but don't go hunting across the whole map.
                var enemy = world.NearestEnemy(this, Size + AttackRange)
                         ?? world.NearestIntruder(this, World.AggroRange);
                if (enemy != null)
                {
                    TargetEnemy = enemy;
                    State = EntityState.Fighting;
                    MoveToward(world, enemy.Bounds);
                    return;
                }

            }

            // === PRIORITY 3: Take what we can't grow ===
            // A fighter goes for somebody else's plot either because our own larder is
            // short — for a team that never learned to farm, the only way to get one at
            // all — or simply because it came across theirs. Hold it long enough and it
            // changes hands, tiles and all.
            if (Brain.Has(AbilityFight))
            {
                var plot = world.NearestEnemyPlot(this);
                if (plot != null)
                {
                    bool needed = world.GetTeamFood(TeamId) < World.RaidThreshold ||
                                  world.GetTeamWater(TeamId) < World.RaidThreshold;
                    bool stumbledOn = DistanceTo(plot.Value) <= World.ContestRange;

                    if (needed || stumbledOn)
                    {
                        State = EntityState.Raiding;
                        MoveToward(world, plot.Value);
                        return;
                    }
                }
            }

            // === PRIORITY 4: Carry it to the clearing ===
            // Full and watered, so head for the forest. Both farming and breeding
            // happen on arrival — World.HandleFarming turns a full belly into a food
            // tile and a slaked thirst into a water tile, and pairs breed there.
            var clearing = world.GetNest(TeamId) ?? FindNearestForestBubble(world.Forest);
            if (clearing != null)
            {
                State = ReadyToReproduce ? EntityState.SearchingMate : EntityState.Moving;
                MoveToward(world, clearing.Value);
                return;
            }

            // === Fallback: Explore ===
            State = EntityState.Moving;
            RandomMove(world);
        }

        // =========================
        // === HELPER METHODS    ===
        // =========================
        // A bubble is worth walking to if it still holds something and is either
        // unclaimed, ours, or somebody else's that we're equipped to take.
        private bool IsEdible(ResourceBubble b) =>
            b.Value > 0 &&
            (!b.OwnerTeamId.HasValue || b.OwnerTeamId.Value == TeamId || Brain.Has(AbilityFight));

        private bool IsTouching(params Area[] areas)
        {
            foreach (var area in areas)
            {
                if (area?.Bubbles == null) continue;
                foreach (var b in area.Bubbles)
                    if (IsEdible(b) && Bounds.IntersectsWith(b.Bounds)) return true;
            }
            return false;
        }

        public ResourceBubble FindNearest(params Area[] areas)
        {
            ResourceBubble best = null;
            double bestDist = double.MaxValue;
            foreach (var area in areas)
            {
                if (area?.Bubbles == null) continue;
                foreach (var b in area.Bubbles)
                {
                    if (!IsEdible(b)) continue; // empty, or owned by the other team
                    double cx = b.Bounds.X + b.Bounds.Width / 2.0;
                    double cy = b.Bounds.Y + b.Bounds.Height / 2.0;
                    double dx = cx - CenterX, dy = cy - CenterY;
                    double dist = dx * dx + dy * dy;
                    if (dist < bestDist) { best = b; bestDist = dist; }
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
                double dx = s.X + s.Width / 2.0 - CenterX, dy = s.Y + s.Height / 2.0 - CenterY;
                double dist = dx * dx + dy * dy;
                if (dist < bestDist) { best = s; bestDist = dist; }
            }
            return best;
        }

        private void MoveToward(World world, Rectangle target)
        {
            int cx = target.X + target.Width / 2;
            int cy = target.Y + target.Height / 2;

            int newX = X, newY = Y;
            if (X < cx) newX += Step;
            if (X > cx) newX -= Step;
            if (Y < cy) newY += Step;
            if (Y > cy) newY -= Step;

            if (!CanStand(world, newX, newY)) { RandomMove(world); return; }
            X = newX; Y = newY;
        }

        private void RandomMove(World world)
        {
            int dir = rand.Next(4);
            int newX = X, newY = Y;
            if (dir == 0) newY -= Step;
            else if (dir == 1) newY += Step;
            else if (dir == 2) newX += Step;
            else newX -= Step;

            if (CanStand(world, newX, newY)) { X = newX; Y = newY; }
        }

        private bool CanStand(World world, int x, int y)
        {
            var next = new Rectangle(x, y, Size, Size);
            if (!world.Bounds.Contains(next) || world.Reserved.IntersectsWith(next)) return false;

            // Only entities that evolved a nose for poison refuse to step in it.
            // The rest walk in and die — which is precisely the selection pressure.
            if (Brain.Has(AbilitySensePoison) &&
                world.Poison.StaticBubbles.Any(s => next.IntersectsWith(s))) return false;

            return true;
        }
    }
}

using Evolution.Base;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Evolution
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        /// <summary>
        /// The main entry point. With no arguments this shows the simulation;
        /// with --sim it runs batches headless, which is far faster for tuning.
        ///
        ///   Evolution.exe --sim [runs] [maxTicks] [--out file]
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--sim") { RunBatch(args); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

        private static void RunBatch(string[] args)
        {
            AttachConsole(-1);   // so Console output lands in the calling shell

            int runs = ArgInt(args, 1, 20);
            int maxTicks = ArgInt(args, 2, 20000);
            string outPath = null;
            int outIdx = Array.IndexOf(args, "--out");
            if (outIdx >= 0 && outIdx + 1 < args.Length) outPath = args[outIdx + 1];

            var report = new StringBuilder();
            var results = new List<World>();
            var tickCounts = new List<int>();

            report.AppendLine("seed  ticks  outcome                          T0  T1  births  poisoned  killed  tiles  raided  captured  1stTile  1stBirth");
            report.AppendLine(new string('-', 126));

            for (int seed = 1; seed <= runs; seed++)
            {
                var world = new World(800, 600, new Rectangle(10, 10, 160, 160), seed);
                while (world.Outcome == null && world.TickCount < maxTicks) world.Tick();

                results.Add(world);
                tickCounts.Add(world.TickCount);

                string outcome = world.Outcome == null ? "TIMEOUT (stalemate)" : world.Outcome.Message;
                report.AppendLine(string.Format(
                    "{0,4}  {1,5}  {2,-32} {3,3} {4,3}  {5,6}  {6,8}  {7,6}  {8,5}  {9,6}  {10,8}  {11,7}  {12,8}",
                    seed, world.TickCount, Truncate(outcome, 32),
                    world.Population(0), world.Population(1),
                    world.Births, world.PoisonDeaths, world.CombatDeaths,
                    world.TilesBuilt, world.TilesRaided, world.PlotsCaptured,
                    world.FirstTileTick < 0 ? "-" : world.FirstTileTick.ToString(),
                    world.FirstBirthTick < 0 ? "-" : world.FirstBirthTick.ToString()));
            }

            int timeouts = results.Count(w => w.Outcome == null);
            int byPop = results.Count(w => w.Outcome != null && w.Outcome.Title == "Victory");
            int byWipe = results.Count(w => w.Outcome != null && w.Outcome.Title == "Game Over");
            int noBirths = results.Count(w => w.Births == 0);

            report.AppendLine();
            report.AppendLine($"runs                : {runs}   (maxTicks {maxTicks})");
            report.AppendLine($"population victory  : {byPop}");
            report.AppendLine($"elimination         : {byWipe}");
            report.AppendLine($"stalemate (timeout) : {timeouts}");
            report.AppendLine($"runs with 0 births  : {noBirths}");
            report.AppendLine($"median ticks        : {Median(tickCounts)}");
            report.AppendLine($"avg births / run    : {results.Average(w => w.Births):F1}");
            report.AppendLine($"avg combat deaths   : {results.Average(w => w.CombatDeaths):F1}");
            report.AppendLine($"avg poison deaths   : {results.Average(w => w.PoisonDeaths):F1}");
            report.AppendLine($"avg starved         : {results.Average(w => w.StarvationDeaths):F1}");
            report.AppendLine($"avg tiles built     : {results.Average(w => w.TilesBuilt):F1}");
            report.AppendLine($"avg raid hits       : {results.Average(w => w.TilesRaided):F1}");
            report.AppendLine($"avg plots captured  : {results.Average(w => w.PlotsCaptured):F1}");
            report.AppendLine($"median 1st tile     : {Median(results.Where(w => w.FirstTileTick >= 0).Select(w => w.FirstTileTick).ToList())} ticks");

            report.AppendLine();
            report.AppendLine("ability share of surviving population (founder odds in brackets):");
            var traits = new Dictionary<string, byte>
            {
                { "sense poison", Entity.AbilitySensePoison },
                { "fight",        Entity.AbilityFight },
                { "farm",         Entity.AbilityFarm },
                { "irrigate",     Entity.AbilityIrrigate },
                { "breed",        Entity.AbilityReproduce },
            };
            var founderOdds = new Dictionary<string, string>
            {
                { "sense poison", "0.50" }, { "fight", "0.50" }, { "farm", "0.40" },
                { "irrigate", "0.40" }, { "breed", "0.80" },
            };
            foreach (var t in traits)
            {
                var live = results.Where(w => w.Entities.Count > 0).ToList();
                double share = live.Count == 0 ? 0 : live.Average(w => w.AbilityShare(t.Value));
                report.AppendLine($"  {t.Key,-13}: {share:F2}   (founders {founderOdds[t.Key]})");
            }

            string text = report.ToString();
            Console.WriteLine(text);
            if (outPath != null) File.WriteAllText(outPath, text);
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        private static int ArgInt(string[] args, int index, int fallback)
        {
            int value;
            return index < args.Length && int.TryParse(args[index], out value) ? value : fallback;
        }

        private static int Median(List<int> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Ручка, которую учим: поле BotSettings основного бота + набор вариантов.</summary>
    public class Knob
    {
        public string field; public object[] arms;
        public Knob(string f, params object[] a) { field = f; arms = a; }
    }

    /// <summary>Постоянная память (между играми): статистика вариантов по ручкам.</summary>
    public static class Memory
    {
        public class Arm { public int n; public double mean; }
        public static Dictionary<string, Arm> Arms = new Dictionary<string, Arm>();
        private static bool loaded;
        public static string Dir { get { return GenFilePaths.ConfigFolderPath; } }
        private static string StatsPath { get { return Path.Combine(Dir, "RimBotBrain.txt"); } }
        public static string JournalPath { get { return Path.Combine(Dir, "RimBotJournal.txt"); } }

        public static void Load()
        {
            if (loaded) return; loaded = true;
            try
            {
                if (!File.Exists(StatsPath)) return;
                foreach (string line in File.ReadAllLines(StatsPath))
                {
                    var p = line.Split('|'); if (p.Length != 3) continue;
                    Arms[p[0]] = new Arm { n = int.Parse(p[1]), mean = double.Parse(p[2], CultureInfo.InvariantCulture) };
                }
            }
            catch (Exception e) { T.Warn("Память не прочитана: " + e.Message); }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllLines(StatsPath, Arms.Select(kv => kv.Key + "|" + kv.Value.n + "|" + kv.Value.mean.ToString(CultureInfo.InvariantCulture)).ToArray());
            }
            catch (Exception e) { T.Warn("Память не записана: " + e.Message); }
        }

        public static void Update(string key, double reward)
        {
            Arm a; if (!Arms.TryGetValue(key, out a)) { a = new Arm(); Arms[key] = a; }
            a.n++; a.mean += (reward - a.mean) / a.n;
        }

        public static void Journal(string text)
        {
            try
            {
                int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                File.AppendAllText(JournalPath, "[день " + (tick / 60000) + ", " + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            }
            catch { }
            T.Info(text, "решение");
        }
    }

    /// <summary>Доступ к BotSettings основного бота через reflection.</summary>
    public static class BotSettingsAccess
    {
        private static object Settings()
        {
            var t = T.FindType("RimWorldBot.BotGameComponent");
            var inst = t?.GetField("Instance")?.GetValue(null);
            return t?.GetField("settings")?.GetValue(inst);
        }

        public static bool Set(string field, object value)
        {
            try
            {
                object st = Settings(); if (st == null) return false;
                FieldInfo f = st.GetType().GetField(field); if (f == null) return false;
                f.SetValue(st, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }

        public static object Get(string field)
        {
            try { object st = Settings(); return st?.GetType().GetField(field)?.GetValue(st); } catch { return null; }
        }
    }

    /// <summary>
    /// Мозг: меряет, как идёт игра; раз в эпоху (5 дней) пробует другой вариант одной ручки бота,
    /// оценивает исход (смерти, упавшие, голод, срывы, рост богатства) и запоминает — в том числе для будущих игр.
    /// </summary>
    public class BrainComponent : GameComponent
    {
        public const int EpochTicks = 300000;
        public static readonly Knob[] Knobs =
        {
            new Knob("retreatStrengthRatio", 0.3f, 0.5f, 0.8f),
            new Knob("defenseRadius", 10, 15, 22),
            new Knob("wallThickness", 1, 2),
            new Knob("minFoodDays", 8, 14, 22),
            new Knob("minMedicineCount", 8, 16, 30),
            new Knob("minSteelCount", 200, 500, 1000),
            new Knob("targetColonists", 6, 10, 16),
            new Knob("useKillbox", false, true),
            new Knob("allowTraps", true, false),
            new Knob("allowTurretBuilding", true, false),
        };

        // сохраняется в сейве
        private int epochStart = -1, knobIdx, testArm;
        private float wealthStart;
        private int deaths, downedSamples, hungrySamples, breakSamples, samples, epochNo;
        private List<int> knownColonists = new List<int>();
        private bool endedLogged;
        private int nextSample, nextCrisis;

        public BrainComponent(Game game) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref epochStart, "epochStart", -1);
            Scribe_Values.Look(ref knobIdx, "knobIdx");
            Scribe_Values.Look(ref testArm, "testArm");
            Scribe_Values.Look(ref wealthStart, "wealthStart");
            Scribe_Values.Look(ref deaths, "deaths");
            Scribe_Values.Look(ref downedSamples, "downedSamples");
            Scribe_Values.Look(ref hungrySamples, "hungrySamples");
            Scribe_Values.Look(ref breakSamples, "breakSamples");
            Scribe_Values.Look(ref samples, "samples");
            Scribe_Values.Look(ref epochNo, "epochNo");
            Scribe_Values.Look(ref endedLogged, "endedLogged");
            Scribe_Collections.Look(ref knownColonists, "knownColonists", LookMode.Value);
            if (knownColonists == null) knownColonists = new List<int>();
        }

        public override void GameComponentTick()
        {
            var s = TradeEmpireMod.S;
            if (s == null || !s.enabled || !s.brain) return;
            int tick = Find.TickManager.TicksGame;
            if (tick % 120 != 0) return;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            if (!T.BotAllows()) return;
            if (s.watchdog) T.Safe("Watchdog", () => Watchdog.Sample(map));
            if (s.takeover) T.Safe("Survival", () => Survival.Run(map, tick));

            T.Safe("Crisis", () =>
            {
                if (tick < nextCrisis) return;
                nextCrisis = tick + 600;
                Crisis.Run(map); Opening.Run(map);
                T.Safe("Needs", () => Needs.Run(map));
                T.Safe("Events", () => Events.Run(map));
                T.Safe("Assess", () => Mind.Assess(map));
            });
            T.Safe("Sample", () => { if (tick >= nextSample) { nextSample = tick + 1000; Sample(map); } });
            if (s.learning) T.Safe("Learn", () => Learn(map, tick));
        }

        public override void GameComponentOnGUI()
        {
            T.Safe("Hud", () => Mind.OnGUI());
        }

        private void Sample(Map map)
        {
            var cols = map.mapPawns.FreeColonists.ToList();
            var ids = cols.Select(p => p.thingIDNumber).ToList();
            foreach (int id in knownColonists)
                if (!ids.Contains(id))
                {
                    deaths++;
                    Pawn dead = Find.WorldPawns.AllPawnsDead.FirstOrDefault(p => p.thingIDNumber == id);
                    string why = dead != null && dead.health != null && dead.health.hediffSet != null
                        ? string.Join(", ", dead.health.hediffSet.hediffs.Take(3).Select(h => h.LabelCap).ToArray()) : "неизвестно";
                    Memory.Journal("ПОТЕРЯ: " + (dead != null ? dead.LabelShort : "колонист #" + id) + " (" + why + "). Колонистов осталось: " + cols.Count);
                }
            knownColonists = ids;
            samples++;
            foreach (Pawn p in cols)
            {
                if (p.Downed) downedSamples++;
                if (p.needs != null && p.needs.food != null && p.needs.food.CurLevelPercentage < 0.10f) hungrySamples++;
                if (p.InMentalState) breakSamples++;
            }
            if (cols.Count == 0 && !endedLogged)
            {
                endedLogged = true;
                Memory.Journal("КОНЕЦ: колония погибла на " + (Find.TickManager.TicksGame / 60000) + "-й день. Эпох пройдено: " + epochNo + ". Потерь: " + deaths);
                Memory.Save();
            }
        }

        private void Learn(Map map, int tick)
        {
            Memory.Load();
            if (map.mapPawns.FreeColonistsCount == 0) return;
            if (epochStart < 0) { StartEpoch(map, tick); return; }
            if (tick - epochStart < EpochTicks) return;

            // конец эпохи: считаем награду выбранного варианта
            float wealth = map.wealthWatcher.WealthTotal;
            double reward = (wealth - wealthStart) / 1000.0 - 30.0 * deaths - 1.0 * downedSamples - 1.5 * hungrySamples - 2.0 * breakSamples;
            Knob k = Knobs[knobIdx % Knobs.Length];
            string key = k.field + "#" + testArm;
            Memory.Update(key, reward);
            Memory.Save();
            epochNo++;
            Memory.Journal("Эпоха " + epochNo + ": " + k.field + "=" + k.arms[testArm] + " → награда " + reward.ToString("0.0")
                + " (потери " + deaths + ", упавших " + downedSamples + ", голод " + hungrySamples + ", срывы " + breakSamples
                + ", богатство " + (int)wealthStart + "→" + (int)wealth + ")");
            StartEpoch(map, tick);
        }

        private void StartEpoch(Map map, int tick)
        {
            epochStart = tick; wealthStart = map.wealthWatcher.WealthTotal;
            deaths = downedSamples = hungrySamples = breakSamples = 0;
            // все ручки — на лучшие известные варианты (эксплуатация)
            foreach (Knob k in Knobs)
            {
                int best = BestArm(k);
                BotSettingsAccess.Set(k.field, k.arms[best]);
            }
            // одна ручка — на пробу (UCB1)
            knobIdx = (knobIdx + 1) % Knobs.Length;
            Knob t = Knobs[knobIdx];
            testArm = PickUcb(t);
            BotSettingsAccess.Set(t.field, t.arms[testArm]);
            Memory.Journal("Новая эпоха: пробую " + t.field + "=" + t.arms[testArm] + ", остальное — лучшее из опыта.");
        }

        private static int BestArm(Knob k)
        {
            int best = 0; double bm = double.MinValue; bool any = false;
            for (int i = 0; i < k.arms.Length; i++)
            {
                Memory.Arm a;
                if (Memory.Arms.TryGetValue(k.field + "#" + i, out a) && a.n > 0 && a.mean > bm) { bm = a.mean; best = i; any = true; }
            }
            if (!any)
            {   // нет опыта: берём текущее значение бота, иначе среднюю ручку
                object cur = BotSettingsAccess.Get(k.field);
                for (int i = 0; i < k.arms.Length; i++) if (cur != null && cur.ToString() == k.arms[i].ToString()) return i;
                return k.arms.Length / 2;
            }
            return best;
        }

        private static int PickUcb(Knob k)
        {
            double total = 0;
            for (int i = 0; i < k.arms.Length; i++) { Memory.Arm a; if (Memory.Arms.TryGetValue(k.field + "#" + i, out a)) total += a.n; }
            int pick = 0; double bs = double.MinValue;
            for (int i = 0; i < k.arms.Length; i++)
            {
                Memory.Arm a; Memory.Arms.TryGetValue(k.field + "#" + i, out a);
                if (a == null || a.n == 0) return i;           // сначала пробуем всё
                double sc = a.mean + 15.0 * Math.Sqrt(Math.Log(total + 1) / a.n);
                if (sc > bs) { bs = sc; pick = i; }
            }
            return pick;
        }
    }
}

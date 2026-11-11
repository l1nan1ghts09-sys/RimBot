using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Кора головного мозга колонии: раз в несколько сотен тиков собирает картину мира,
    /// выбирает цель, запускает навыки, проверяет результат и учится. Состояние живёт в сейве,
    /// знания — в Config/RimBotMind.txt (общие для всех игр).
    /// </summary>
    public class BrainComponent : GameComponent
    {
        // сохраняется
        private int lastDay = -1;
        private int lastPlannedDay = -1;
        private int rotation;
        private int activeGoalInt;
        private int goalStartTick;
        private int goalStartDay;
        private float goalStartValue;
        private int goalMetricInt;
        private string goalCtx = "";
        private string probeLine = "";
        private string armLine = "";
        private int deaths;
        private bool endedLogged;
        private List<int> knownColonists = new List<int>();
        private List<int> dailyScores = new List<int>();

        // только в памяти
        private int lastPerceive;
        private ColonyWorld world;
        private Probe probe;
        private List<ColonyGoal> goalList = new List<ColonyGoal>();
        private ColonyGoal current;
        private List<KnobDecision> todayDecisions = new List<KnobDecision>();
        private readonly Dictionary<string, int> armIndices = new Dictionary<string, int>();
        private string planSummary = "";
        private readonly Breaker perceiveBreaker = new Breaker("Восприятие");
        private readonly Breaker reflectBreaker = new Breaker("Рефлексия");
        private readonly Breaker goalBreaker = new Breaker("Цели");
        private readonly Breaker skillBreaker = new Breaker("Навыки");
        private int nextSimple;

        public BrainComponent(Game game)
        {
            Skills.Init();
            Knowledge.Load();
            ResearchBoost.Reset();
            Policy.HoldFood = false;       // запрет продажи еды не переносится в новую игру
            Timeline.Clear();
            Crisis.ResetRuntime();
            Opening.ResetRuntime();
            Needs.ResetRuntime();
            Events.ResetRuntime();
            Planner.ResetRuntime();
            Watchdog.ResetRuntime();
            Survival.ResetRuntime();
            Vanilla.ResetRuntime();
            Roofs.ResetRuntime();
            Zones.ResetRuntime();
            Patterns.ResetRuntime();
            Housing.ResetRuntime();
            Infrastructure.ResetRuntime();
            ThreatFix.ResetRuntime();
            Skills.ResetRuntime();
            Learning.ResetRuntime();
            Reflect.ResetRuntime();
            ResearchAdvisor.ResetRuntime();
            Mind.ResetRuntime();
        }

        public static BrainComponent Get()
        {
            try { return Verse.Current.Game != null ? Verse.Current.Game.GetComponent<BrainComponent>() : null; }
            catch { return null; }
        }

        public ColonyWorld World { get { return world; } }
        public List<ColonyGoal> GoalList { get { return goalList; } }
        public ColonyGoal Current { get { return current; } }
        public Probe ActiveProbe { get { return probe; } }
        public List<KnobDecision> TodayDecisions { get { return todayDecisions; } }
        public Dictionary<string, int> ArmIndices { get { return armIndices; } }
        public int Deaths { get { return deaths; } }
        public bool EndedLogged { get { return endedLogged; } set { endedLogged = value; } }
        public string PlanSummary { get { return planSummary; } }

        /// <summary>Сброс обучения: незавершённая проверка больше не нужна.</summary>
        public void ClearProbe() { probe = null; probeLine = ""; if (todayDecisions != null) todayDecisions.Clear(); }
        public List<int> DailyScores { get { return dailyScores; } }
        public int LastPlanDay { get { return lastPlannedDay; } }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                probeLine = probe != null ? probe.Encode() : "";
                armLine = EncodeArms();
            }
            Scribe_Values.Look(ref lastDay, "lastDay", -1);
            Scribe_Values.Look(ref lastPlannedDay, "lastPlannedDay", -1);
            Scribe_Values.Look(ref rotation, "rotation");
            Scribe_Values.Look(ref activeGoalInt, "activeGoalInt");
            Scribe_Values.Look(ref goalStartTick, "goalStartTick");
            Scribe_Values.Look(ref goalStartDay, "goalStartDay");
            Scribe_Values.Look(ref goalStartValue, "goalStartValue");
            Scribe_Values.Look(ref goalMetricInt, "goalMetricInt");
            Scribe_Values.Look(ref goalCtx, "goalCtx", "");
            Scribe_Values.Look(ref probeLine, "probeLine", "");
            Scribe_Values.Look(ref armLine, "armLine", "");
            Scribe_Values.Look(ref deaths, "deaths");
            Scribe_Values.Look(ref endedLogged, "endedLogged");
            Scribe_Collections.Look(ref knownColonists, "knownColonists", LookMode.Value);
            Scribe_Collections.Look(ref dailyScores, "dailyScores", LookMode.Value);
            if (knownColonists == null) knownColonists = new List<int>();
            if (dailyScores == null) dailyScores = new List<int>();

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                probe = Probe.Decode(probeLine);
                DecodeArms(armLine);
            }
        }

        public override void GameComponentTick()
        {
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.enabled || !s.brain) return;
            int tick;
            try { tick = Find.TickManager.TicksGame; } catch { return; }
            if (tick % 120 != 0) return;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            if (!T.BotAllows()) return;

            Knowledge.Load();

            if (s.watchdog) T.Safe("Watchdog", () => Watchdog.Sample(map));

            // ---- восприятие ----
            if (world == null || tick < lastPerceive || tick - lastPerceive >= 600)
            {
                ColonyWorld prev = world;
                ColonyWorld fresh = null;
                perceiveBreaker.Run(() => { fresh = ColonyWorld.Build(map, prev); }, 600);
                if (fresh == null) return;
                world = fresh;
                lastPerceive = tick;
                reflectBreaker.Run(() => Reflect.Step(prev, world, this), 600);
                CheckDeaths(world);
                Mind.Assess(world);
                if (world.pop == 0) return;
            }
            if (world == null) return;

            // Порча еды, ветшание вещей и ванильные механики — в обоих режимах.
            T.Safe("FoodSafety", () => FoodSafety.Run(map, tick));
            T.Safe("Deterioration", () => Deterioration.Run(map, tick));
            T.Safe("Mechanics", () => Mechanics.Run(map, tick));
            T.Safe("Roofs", () => Roofs.Run(map, tick));
            T.Safe("Zones", () => Zones.Run(map, tick));
            T.Safe("Patterns", () => Patterns.Run(map, tick));
            T.Safe("Housing", () => Housing.Run(map, tick));

            // ---- упрощённый режим: только антикризисные модули ----
            if (!s.deepBrain)
            {
                if (tick >= nextSimple)
                {
                    nextSimple = tick + 600;
                    skillBreaker.Run(() => RunSimple(world), 600);
                }
                return;
            }

            // ---- базовые навыки и события ----
            skillBreaker.Run(() => Skills.RunGlobal(world, 3), 600);

            // ---- цели ----
            goalBreaker.Run(() => UpdateGoals(world), 600);

            // ---- действия под цели ----
            skillBreaker.Run(() => Skills.RunForGoals(world, goalList, 2), 600);

            // ---- обучение ----
            if (world.day > lastDay)
            {
                lastDay = world.day;
                AddDailyScore(Learning.DayScore(world));
                T.Safe("Learning", () => Learning.DailyReview(world, ref probe, armIndices, deaths));
                if (probe == null) probeLine = "";
            }
            if (world.day != lastPlannedDay)
            {
                lastPlannedDay = world.day;
                T.Safe("Plan", () => PlanDay(world));
            }

            Knowledge.SaveIfDirty(10000);
        }

        /// <summary>Без «глубокого мозга»: прежнее поведение — кризисы, потребности, события, энергетика и работы.</summary>
        private void RunSimple(ColonyWorld w)
        {
            Mind.Assess(w);
            Crisis.Run(w.map);
            Opening.Run(w.map);
            Needs.Run(w.map);
            Events.Run(w.map);
            if (TradeEmpireMod.S != null && TradeEmpireMod.S.takeover) Survival.Run(w.map, w.tick);
            else
            {
                T.Safe("ThreatFix", () => ThreatFix.Install());
                ThreatFix.ReleaseColonists(w.map, w.tick);
            }
        }

        private void UpdateGoals(ColonyWorld w)
        {
            goalList = Goals.Evaluate(w);
            GoalId currentId = (GoalId)activeGoalInt;
            ColonyGoal picked = Goals.Pick(goalList, currentId, w.tick - goalStartTick);
            current = picked;
            if (picked == null) return;

            if (picked.id != currentId)
            {
                if (currentId != GoalId.None && goalStartTick > 0)
                    Reflect.ReviewGoal(w, currentId, (MetricKind)goalMetricInt, goalCtx, goalStartValue, goalStartTick);
                activeGoalInt = (int)picked.id;
                goalStartTick = w.tick;
                goalStartDay = w.day;
                goalCtx = w.goalCtx;
                goalMetricInt = (int)Goals.MetricOf(picked.id);
                goalStartValue = Learning.MetricValue(w, Goals.MetricOf(picked.id));
                Mind.Think("цель", "Новая главная цель: «" + picked.name + "» — " + picked.why + ". Действие: " + picked.action + ".");
            }
        }

        private void PlanDay(ColonyWorld w)
        {
            KnobDecision pd;
            todayDecisions = Learning.PlanDay(w, armIndices, rotation++, probe, out pd);
            if (pd != null) probe = Learning.StartProbe(w, pd, deaths);
            probeLine = probe != null ? probe.Encode() : "";
            armLine = EncodeArms();
            BuildPlanSummary();
        }

        private void BuildPlanSummary()
        {
            if (todayDecisions == null || todayDecisions.Count == 0)
            {
                planSummary = "Настройки не менялись: текущие варианты признаны лучшими для этих условий.";
                return;
            }
            string text = "";
            int n = 0;
            foreach (KnobDecision d in todayDecisions)
            {
                if (n++ > 4) break;
                text += (text.Length > 0 ? "; " : "") + d.knob.title + " = " + d.knob.ArmText(d.arm) + (d.probe ? " (проверка)" : "");
            }
            planSummary = text;
        }

        private void AddDailyScore(float score)
        {
            dailyScores.Add(Mathf.RoundToInt(U.Clamp01(score) * 1000f));
            if (dailyScores.Count > 90) dailyScores.RemoveRange(0, 30);
        }

        private void CheckDeaths(ColonyWorld w)
        {
            try
            {
                List<int> ids = w.colonists.Select(p => p.thingIDNumber).ToList();
                List<int> keep = new List<int>();
                foreach (int id in knownColonists)
                {
                    if (ids.Contains(id)) { keep.Add(id); continue; }
                    Pawn dead = null;
                    try { dead = Find.WorldPawns.AllPawnsDead.FirstOrDefault(p => p.thingIDNumber == id); } catch { }
                    if (dead != null)
                    {
                        deaths++;
                        Reflect.OnDeath(dead, w);
                    }
                    else keep.Add(id);          // в караване или вне карты — это не смерть
                }
                foreach (int id in ids) if (!keep.Contains(id)) keep.Add(id);
                if (keep.Count > 200) keep.RemoveRange(0, keep.Count - 200);
                knownColonists = keep;
            }
            catch (Exception e) { T.Warn("Учёт потерь неполный: " + e.Message); }
        }

        public override void GameComponentOnGUI()
        {
            T.Safe("Hud", () => Mind.OnGUI());
        }

        private string EncodeArms()
        {
            string text = "";
            foreach (var kv in armIndices)
                text += (text.Length > 0 ? ";" : "") + kv.Key + "=" + kv.Value;
            return text;
        }

        private void DecodeArms(string line)
        {
            armIndices.Clear();
            if (string.IsNullOrEmpty(line)) return;
            foreach (string part in line.Split(';'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string field = part.Substring(0, eq);
                int idx = U.ParseInt(part.Substring(eq + 1), 0);
                armIndices[field] = idx;
            }
        }

        /// <summary>Короткая строка для панели: что бот сейчас делает и почему.</summary>
        public string NowLine()
        {
            if (world == null) return "Мозг: ждёт загрузки карты.";
            if (current == null) return "Мозг: оцениваю обстановку.";
            string text = "Цель «" + current.name + "» (" + U.Pct(current.utility) + "): " + current.why + " → " + current.action;
            if (probe != null)
            {
                Knob k = KnobBank.Find(probe.field);
                if (k != null) text += " | Проверяю: " + k.title + " = " + k.ArmText(probe.arm) + " (итог через "
                    + U.F(Mathf.Max(0f, (probe.startTick + Math.Max(6000, k.window - 6000) - world.tick) / 60000f)) + " дн.)";
            }
            return text;
        }

        public string LearningLine()
        {
            if (world == null) return Learning.Summary();
            return "День " + world.day + ", оценка " + U.Pct(Learning.DayScore(world)) + ". " + Learning.Summary();
        }
    }
}

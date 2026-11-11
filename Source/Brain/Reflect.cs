using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Рефлексия: бот сравнивает «до» и «после», объясняет себе, что произошло,
    /// и превращает это в уроки для этой и следующих колоний.
    /// </summary>
    public static class Reflect
    {
        private static bool lastThreat;
        private static bool lastFoodCrisis;
        private static bool lastPowerOutage;
        private static int lastResearchDone = -1;
        private static float lastWealth = -1f;
        private static int lastMoodBreakDay = -99;
        private static string lastResearch = "";

        public static void ResetRuntime()
        {
            lastThreat = false;
            lastFoodCrisis = false;
            lastPowerOutage = false;
            lastResearchDone = -1;
            lastWealth = -1f;
            lastMoodBreakDay = -99;
            lastResearch = "";
        }

        public static void Step(ColonyWorld prev, ColonyWorld w, BrainComponent brain)
        {
            if (w == null || brain == null) return;

            if (prev != null)
            {
                // угроза
                if (w.threat && !prev.threat)
                {
                    Knowledge.Journal("УГРОЗА: " + w.hostiles + " врагов, их сила " + U.Pct(w.threatRatio) + " от нашей. "
                        + "Турелей " + w.turrets + ", ловушек " + w.traps + ", лекарств " + w.medicine + ".");
                    Mind.Think("событие", "Началась угроза: " + w.hostiles + " врагов (сила " + U.Pct(w.threatRatio) + " от нашей).");
                }
                else if (!w.threat && prev.threat)
                {
                    string result = w.downed > prev.downed ? "потери: лежачих " + w.downed + " (было " + prev.downed + ")"
                        : w.healthScore < prev.healthScore ? "все устояли, но есть ранения" : "отбились без потерь";
                    Knowledge.Journal("УГРОЗА ОТБИТА: " + result + ", колонистов " + w.pop + ". Следующий рейд ожидается примерно через "
                        + U.F(Knowledge.ExpectedRaidInterval) + " дн.");
                    if (w.downed > prev.downed)
                    {
                        Knowledge.AddLesson("raid_losses", "В бою появились лежачие (" + w.downed + "). "
                            + "Вывод: заранее держать укрытия, турели и запас лекарств; не выводить раненых в бой.",
                            0.65f, w.ctx, GoalId.DefensePrep, 0.12f);
                    }
                    else
                    {
                        Knowledge.AddLesson("raid_ok", "Оборона сработала без лежачих при силе врага " + U.Pct(prev.threatRatio)
                            + ". Вывод: текущая тактика обороны подходит.", 0.55f, w.ctx, GoalId.DefensePrep, 0.05f);
                    }
                }

                // еда
                bool foodCrisis = w.foodDays < 2f || w.starving > 0;
                if (foodCrisis && !lastFoodCrisis)
                {
                    Knowledge.AddLesson("food_low", "Запас еды упал до " + U.F(w.foodDays) + " дн. "
                        + "Вывод: держать запас не меньше " + U.F(w.foodTargetDays) + " дн.; продавать еду только выше цели.",
                        0.6f, w.ctx, GoalId.Food, 0.15f);
                }
                if (!foodCrisis && lastFoodCrisis)
                    Mind.Think("еда", "Голодный кризис преодолён: запас " + U.F(w.foodDays) + " дн.");
                lastFoodCrisis = foodCrisis;

                // энергия
                bool outage = w.powerDeficit > 100f;
                if (outage && !lastPowerOutage)
                    Knowledge.Journal("ЭНЕРГИЯ: дефицит " + (int)w.powerDeficit + " Вт при потреблении " + (int)w.powerDemand + " Вт.");
                lastPowerOutage = outage;

                // наука
                if (lastResearchDone < 0) lastResearchDone = w.researchDone;
                else if (w.researchDone > lastResearchDone)
                {
                    int done = w.researchDone - lastResearchDone;
                    Knowledge.Journal("НАУКА: завершено проектов +" + done + ". Всего " + w.researchDone + ".");
                    Knowledge.AddLesson("research_done", "Исследования идут: завершено " + w.researchDone + " проектов. "
                        + "Вывод: текущий выбор направлений работает.", 0.5f, "*", GoalId.Research, 0.04f);
                    lastResearchDone = w.researchDone;
                }
                else lastResearchDone = w.researchDone;

                // срывы
                if (w.mental > prev.mental && w.day - lastMoodBreakDay >= 1)
                {
                    lastMoodBreakDay = w.day;
                    Knowledge.AddLesson("mood_break", "Срыв настроения при среднем " + U.Pct(w.moodAvg) + ". "
                        + "Вывод: держать настроение выше 55%: отдых, еда, свет, комнаты; проверять причины.", 0.6f, w.ctx, GoalId.Mood, 0.12f);
                }

                // богатство
                if (lastWealth >= 0f && w.day > 1 && Math.Abs(w.wealth - lastWealth) > w.wealth * 0.15f && w.wealth > 1000f)
                {
                    Knowledge.Journal("БОГАТСТВО: " + (int)lastWealth + " → " + (int)w.wealth + " (" + U.Sign(w.wealth - lastWealth) + ").");
                }
                lastWealth = w.wealth;
            }
            else
            {
                lastThreat = w.threat;
                lastFoodCrisis = w.foodDays < 2f;
                lastPowerOutage = w.powerDeficit > 100f;
                lastResearchDone = w.researchDone;
                lastWealth = w.wealth;
            }

            lastThreat = w.threat;

            if (w.pop == 0 && !brain.EndedLogged)
            {
                PostMortem(w, brain);
                brain.EndedLogged = true;
                Knowledge.Save(true);
            }
        }

        public static void ReviewGoal(ColonyWorld w, GoalId id, MetricKind metric, string goalCtx, float startValue, int startTick)
        {
            if (w == null || id == GoalId.None) return;
            if (TradeEmpireMod.S != null && !TradeEmpireMod.S.learning) return;
            int ticks = w.tick - startTick;
            if (ticks < 30000) return;                    // меньше полудня — слишком шумно
            float days = ticks / 60000f;
            float end = Learning.MetricValue(w, metric);
            float delta = end - startValue;
            // разные метрики в разных масштабах
            switch (metric)
            {
                case MetricKind.Food: delta /= 2f; break;
                case MetricKind.Mood: delta /= 10f; break;
                case MetricKind.Health: delta /= 10f; break;
                case MetricKind.Population: delta /= 3f; break;
                case MetricKind.Wealth: delta /= 3f; break;
                default: break;
            }
            Learning.ObserveGoal(id, goalCtx, delta, Mathf.Max(1, (int)days));
        }

        public static void OnDeath(Pawn dead, ColonyWorld w)
        {
            string why = "неизвестно";
            string cause = "unknown";
            try
            {
                if (dead != null && dead.health != null && dead.health.hediffSet != null)
                {
                    var hediffs = dead.health.hediffSet.hediffs;
                    if (hediffs != null && hediffs.Count > 0)
                        why = string.Join(", ", hediffs.Take(4).Select(h => h.LabelCap.ToString()).ToArray());
                    if (dead.health.hediffSet.HasHediff(HediffDefOf.Hypothermia)) cause = "cold";
                    else if (dead.health.hediffSet.BleedRateTotal > 0.2f) cause = "combat";
                }
                if (w != null && (w.threat || w.bleeding > 0)) cause = "combat";
                if (w != null && w.foodDays < 1.5f) cause = "food";
            }
            catch { }

            GoalId goal = cause == "cold" ? GoalId.Warmth : cause == "food" ? GoalId.Food
                : cause == "combat" ? GoalId.DefensePrep : GoalId.Medical;
            string name = dead != null ? dead.LabelShort : "колонист";
            Knowledge.Journal("ПОТЕРЯ: " + name + " (" + why + "). Причина по оценке бота: " + CauseText(cause)
                + ". Колонистов осталось: " + (w != null ? w.pop : 0));
            Knowledge.AddLesson("death:" + cause,
                "Погиб " + name + " — " + CauseText(cause) + " (" + why + "). Вывод: усилить цель «" + Goals.Title(goal)
                + "» заранее, не доводить до крайности.", 0.7f, w != null ? w.ctx : "*", goal, 0.18f);
            if (w != null) Knowledge.LearnGoalWeight(goal, w.goalCtx, 0.22f);
            Mind.Think("потеря", "Погиб " + name + ": " + why + ". Урок записан, приоритет «" + Goals.Title(goal) + "» повышен.");
        }

        private static string CauseText(string cause)
        {
            switch (cause)
            {
                case "cold": return "переохлаждение";
                case "food": return "голод";
                case "combat": return "бой/кровопотеря";
                default: return "болезнь или несчастный случай";
            }
        }

        public static void PostMortem(ColonyWorld w, BrainComponent brain)
        {
            try
            {
                int day = w != null ? w.day : (Find.TickManager.TicksGame / 60000);
                string cause = "неизвестно";
                GoalId goal = GoalId.None;
                if (w != null)
                {
                    if (w.starving > 0 || w.foodDays < 1f) { cause = "голод (запас " + U.F(w.foodDays) + " дн.)"; goal = GoalId.Food; }
                    else if (w.hypothermia > 0) { cause = "переохлаждение"; goal = GoalId.Warmth; }
                    else if (w.heatstroke > 0) { cause = "перегрев"; goal = GoalId.Warmth; }
                    else if (w.threat || w.bleeding > 0) { cause = "боевые потери"; goal = GoalId.DefensePrep; }
                    else if (w.medicine == 0 || w.needsTend > 0) { cause = "болезнь без лекарств"; goal = GoalId.Medical; }
                    else if (w.mental > 0 || w.moodAvg < 0.25f) { cause = "срыв настроения"; goal = GoalId.Mood; }
                }

                Knowledge.Journal("КОНЕЦ КОЛОНИИ: день " + day + ", потеряно колонистов " + brain.Deaths
                    + ", вероятная причина: " + cause + ". Эпох обучения: " + (w != null ? w.day / 5 : 0) + "."
                    + " Накопленный опыт сохранён — следующая колония начнёт умнее.");
                Knowledge.AddLesson("colony_end:" + goal,
                    "Колония погибла на " + day + "-й день. Вероятная причина: " + cause
                    + ". Вывод: в следующей игре держать этот риск под контролем с первых дней.",
                    0.85f, w != null ? w.ctx : "*", goal, 0.25f);
                if (goal == GoalId.Food)
                {
                    Knowledge.AddLesson("plan_food", "Держать запас еды не меньше 12–16 дней и не продавать продовольствие ниже цели.",
                        0.8f, "*", GoalId.Food, 0.2f);
                }
                else if (goal == GoalId.DefensePrep)
                {
                    Knowledge.AddLesson("plan_defense", "Строить укрытия/турели/ловушки до прихода рейда: богатство повышает силу врагов.",
                        0.8f, "*", GoalId.DefensePrep, 0.2f);
                }
                else if (goal == GoalId.Warmth)
                {
                    Knowledge.AddLesson("plan_warmth", "Готовить тепло заранее: костёр/обогрев и тёплая одежда до морозов.",
                        0.8f, "*", GoalId.Warmth, 0.2f);
                }
                else if (goal == GoalId.Medical)
                {
                    Knowledge.AddLesson("plan_medical", "Держать запас лекарств и врача с приоритетом; лечить до осложнений.",
                        0.8f, "*", GoalId.Medical, 0.2f);
                }

                Knowledge.Save(true);
                Mind.Think("итог", "Колония погибла. Разбор записан в дневник; уроки повысят приоритет «" + Goals.Title(goal) + "» в следующих играх.");
            }
            catch (Exception e) { T.Warn("Разбор гибели колонии не записан: " + e.Message); }
        }

        public static string CausesLine(ColonyWorld w)
        {
            if (w == null) return "";
            if (w.threat) return "Главный риск: бой (" + w.hostiles + " врагов).";
            if (w.foodDays < 3f) return "Главный риск: голод (" + U.F(w.foodDays) + " дн.).";
            if (w.hypothermia > 0) return "Главный риск: холод.";
            if (w.medicine < 5 && w.needsTend > 0) return "Главный риск: болезни без лекарств.";
            if (w.moodAvg < 0.35f) return "Главный риск: срыв настроения.";
            return "Явных смертельных рисков нет; главное — не потерять запас еды и оборону.";
        }
    }
}

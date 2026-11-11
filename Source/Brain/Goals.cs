using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    public enum GoalId
    {
        None, Fire, Threat, Medical, Starvation, Food, Warmth, Mood, Rest, Power, Housing,
        Research, Economy, DefensePrep, Recruit, Ideology, Clean, Prisoners, Animals
    }

    public class ColonyGoal
    {
        public GoalId id;
        public string name = "";
        public float utility;
        public float raw;
        public float weight = 1f;
        public string why = "";
        public string action = "";
        public bool critical;
        public MetricKind metric = MetricKind.General;

        public string Line()
        {
            return name + ": " + U.Pct(utility) + " — " + why + " → " + action;
        }
    }

    /// <summary>
    /// Набор целей колонии. Каждая цель — не жёсткая команда, а «насколько сейчас важно»
    /// с объяснением. Выбор — argmax с памятью (гистерезис), чтобы бот не дёргался.
    /// </summary>
    public static class Goals
    {
        private static readonly GoalId[] Order =
        {
            GoalId.Fire, GoalId.Threat, GoalId.Medical, GoalId.Starvation, GoalId.Food, GoalId.Warmth,
            GoalId.Mood, GoalId.Rest, GoalId.Power, GoalId.Housing, GoalId.Research, GoalId.Economy,
            GoalId.DefensePrep, GoalId.Recruit, GoalId.Ideology, GoalId.Clean, GoalId.Prisoners, GoalId.Animals
        };

        public static string Title(GoalId id)
        {
            switch (id)
            {
                case GoalId.Fire: return "Пожар";
                case GoalId.Threat: return "Оборона";
                case GoalId.Medical: return "Медицина";
                case GoalId.Starvation: return "Голод";
                case GoalId.Food: return "Продовольствие";
                case GoalId.Warmth: return "Тепло";
                case GoalId.Mood: return "Настроение";
                case GoalId.Rest: return "Отдых";
                case GoalId.Power: return "Энергия";
                case GoalId.Housing: return "Жильё";
                case GoalId.Research: return "Наука";
                case GoalId.Economy: return "Экономика";
                case GoalId.DefensePrep: return "Подготовка обороны";
                case GoalId.Recruit: return "Рост колонии";
                case GoalId.Ideology: return "Идеология";
                case GoalId.Clean: return "Чистота";
                case GoalId.Prisoners: return "Пленные";
                case GoalId.Animals: return "Животные";
                default: return "Нет";
            }
        }

        public static MetricKind MetricOf(GoalId id)
        {
            switch (id)
            {
                case GoalId.Fire: return MetricKind.Health;
                case GoalId.Threat: return MetricKind.Defense;
                case GoalId.Medical: return MetricKind.Health;
                case GoalId.Starvation: return MetricKind.Food;
                case GoalId.Food: return MetricKind.Food;
                case GoalId.Warmth: return MetricKind.Health;
                case GoalId.Mood: return MetricKind.Mood;
                case GoalId.Rest: return MetricKind.Mood;
                case GoalId.Power: return MetricKind.Power;
                case GoalId.Housing: return MetricKind.Housing;
                case GoalId.Research: return MetricKind.Research;
                case GoalId.Economy: return MetricKind.Wealth;
                case GoalId.DefensePrep: return MetricKind.Defense;
                case GoalId.Recruit: return MetricKind.Population;
                case GoalId.Ideology: return MetricKind.Mood;
                case GoalId.Clean: return MetricKind.Mood;
                case GoalId.Prisoners: return MetricKind.Population;
                case GoalId.Animals: return MetricKind.Food;
                default: return MetricKind.General;
            }
        }

        private static float DefaultWeight(GoalId id)
        {
            switch (id)
            {
                case GoalId.Fire: return 1.7f;
                case GoalId.Threat: return 1.5f;
                case GoalId.Medical: return 1.35f;
                case GoalId.Starvation: return 1.5f;
                case GoalId.Food: return 1.05f;
                case GoalId.Warmth: return 1.2f;
                case GoalId.Mood: return 0.95f;
                case GoalId.Rest: return 0.85f;
                case GoalId.Power: return 0.95f;
                case GoalId.Housing: return 0.75f;
                case GoalId.Research: return 0.75f;
                case GoalId.Economy: return 0.85f;
                case GoalId.DefensePrep: return 0.85f;
                case GoalId.Recruit: return 0.55f;
                case GoalId.Ideology: return 0.4f;
                case GoalId.Clean: return 0.3f;
                case GoalId.Prisoners: return 0.4f;
                case GoalId.Animals: return 0.35f;
                default: return 0.3f;
            }
        }

        private static int MetaFeature(GoalId id)
        {
            switch (id)
            {
                case GoalId.Food:
                case GoalId.Starvation: return 0;
                case GoalId.Medical:
                case GoalId.Warmth: return 1;
                case GoalId.Mood:
                case GoalId.Rest: return 2;
                case GoalId.Economy:
                case GoalId.Research: return 3;
                case GoalId.Threat:
                case GoalId.Fire:
                case GoalId.DefensePrep: return 4;
                default: return 5;
            }
        }

        public static List<ColonyGoal> Evaluate(ColonyWorld w)
        {
            List<ColonyGoal> list = new List<ColonyGoal>();
            if (w == null || w.map == null) return list;

            int fires = 0;
            try { fires = w.map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count; } catch { }

            float researchTarget = 24f;
            int targetColonists = Math.Max(6, T.BotInt("targetColonists", 10));
            float foodGap = w.foodTargetDays - w.foodDays;
            float trendPenalty = w.foodTrend < 0f ? Mathf.Clamp01(-w.foodTrend / 2f) * 0.25f : 0f;

            foreach (GoalId id in Order)
            {
                ColonyGoal g = new ColonyGoal { id = id, name = Title(id) };
                switch (id)
                {
                    case GoalId.Fire:
                        g.raw = fires > 0 ? 1f : 0f;
                        g.critical = fires > 0;
                        g.why = fires > 0 ? ("горит " + fires + " очаг(ов)") : "пожаров нет";
                        g.action = "тушение приоритетом 1, эвакуация раненых";
                        g.metric = MetricKind.Health;
                        break;

                    case GoalId.Threat:
                        g.raw = w.threat
                            ? (w.hostilesNear > 0 ? U.Clamp01(0.65f + w.threatRatio * 0.45f) : U.Clamp01(0.22f + w.threatRatio * 0.25f))
                            : 0.02f;
                        g.critical = w.threat && (w.hostilesNear > 0 || w.threatRatio > 0.8f);
                        g.why = w.threat ? (w.hostiles + " врагов, наша сила " + U.Pct(w.threatRatio) + " от их") : "врагов на карте нет";
                        g.action = w.threat ? "сбор защитников, укрытия, отступление слабых" : "укрепления и подготовка";
                        g.metric = MetricKind.Defense;
                        break;

                    case GoalId.Medical:
                        g.raw = Mathf.Max(w.healthUrgency, w.downed > 0 ? 0.95f : w.bleeding > 0 ? 0.85f : w.needsTend > 0 ? 0.55f : 0f);
                        g.critical = w.downed > 0 || w.bleeding > 1;
                        g.why = "лежачих " + w.downed + ", кровотечений " + w.bleeding + ", нужен врач " + w.needsTend;
                        g.action = "врач, кровати, лекарства; раненые отдыхают";
                        g.metric = MetricKind.Health;
                        break;

                    case GoalId.Starvation:
                        g.raw = w.starving > 0 ? 1f : w.foodDays < 1.5f ? 0.9f : w.foodDays < 2.5f ? 0.6f : 0f;
                        g.critical = w.starving > 0 || w.foodDays < 1.5f;
                        g.why = "еды на " + U.F(w.foodDays) + " дн., голодают " + w.starving + ", тренд " + U.Sign(w.foodTrend) + "/дн.";
                        g.action = "сбор дикой еды, охота, рубка, запрет продажи еды";
                        g.metric = MetricKind.Food;
                        break;

                    case GoalId.Food:
                        g.raw = U.Clamp01(foodGap / Mathf.Max(4f, w.foodTargetDays) + trendPenalty);
                        g.why = "запас " + U.F(w.foodDays) + "/" + U.F(w.foodTargetDays) + " дн., с урожаем " + U.F(w.foodDaysWithCrops)
                            + ", тренд " + U.Sign(w.foodTrend) + "/дн.";
                        g.action = "грядки, готовка, охота; расширяю запас";
                        g.metric = MetricKind.Food;
                        break;

                    case GoalId.Warmth:
                    {
                        float t = w.outdoorTemp;
                        float stress = Mathf.Max(0f, Mathf.Abs(t - 21f) - 18f) / 22f;
                        g.raw = U.Clamp01(stress + w.hypothermia * 0.35f + w.heatstroke * 0.35f);
                        g.critical = w.hypothermia > 0 || w.heatstroke > 0;
                        g.why = "на улице " + t.ToString("0") + "°C, переохлаждение " + w.hypothermia + ", перегрев " + w.heatstroke;
                        g.action = t < 0f ? "костёр, тёплая одежда, работа в помещениях" : "работа в тени/помещениях";
                        g.metric = MetricKind.Health;
                        break;
                    }

                    case GoalId.Mood:
                        g.raw = w.moodUrgency;
                        g.critical = w.mental > 0;
                        g.why = "среднее настроение " + U.Pct(w.moodAvg) + ", срывов " + w.mental;
                        g.action = "отдых, радость, вечеринка; проверяю причины";
                        g.metric = MetricKind.Mood;
                        break;

                    case GoalId.Rest:
                        g.raw = U.Clamp01((0.28f - w.restMin) * 3.4f + w.exhausted * 0.05f);
                        g.why = "минимум отдыха " + U.Pct(w.restMin) + ", вымотаны " + w.exhausted;
                        g.action = "сон по расписанию, снять лишние задачи";
                        g.metric = MetricKind.Mood;
                        break;

                    case GoalId.Power:
                        g.raw = U.Clamp01(w.powerDeficit / Mathf.Max(200f, w.powerDemand * 0.5f));
                        g.why = "мощность " + (int)w.powerCapacity + " Вт, нужно " + (int)w.powerDemand + " Вт, дефицит " + (int)w.powerDeficit;
                        g.action = "один генератор, провода, лимит лишних";
                        g.metric = MetricKind.Power;
                        break;

                    case GoalId.Housing:
                        g.raw = w.beds < w.pop ? U.Clamp01((w.pop - w.beds) / Mathf.Max(1f, w.pop)) : 0f;
                        g.why = "кроватей " + w.beds + " на " + w.pop + " человек";
                        g.action = "кровати и комнаты, без перегруза строек";
                        g.metric = MetricKind.Housing;
                        break;

                    case GoalId.Research:
                        g.raw = w.researchAvailable > 0 ? U.Clamp01(0.3f + (1f - U.Clamp01(w.researchDone / researchTarget)) * 0.5f) : 0f;
                        if (BotBridge.CurrentResearch() == null && w.researchAvailable > 0) g.raw = Mathf.Max(g.raw, 0.55f);
                        g.why = "исследовано " + w.researchDone + ", доступно " + w.researchAvailable + ", текущий: " + w.researchCurrent;
                        g.action = "выбор самого полезного проекта по ценности";
                        g.metric = MetricKind.Research;
                        break;

                    case GoalId.Economy:
                        g.raw = U.Clamp01(0.35f + (w.wealthTrend < 0f ? 0.25f : 0f) + (w.tablesWithoutBills > 0 ? 0.15f : 0f)
                            + (w.bills == 0 && w.tables > 0 ? 0.15f : 0f));
                        g.why = "богатство " + (int)w.wealth + " (" + U.Sign(w.wealthTrend) + "/дн.), верстаков " + w.tables
                            + " (без заданий " + w.tablesWithoutBills + "), заданий " + w.bills;
                        g.action = "прибыльные задания, торговля, верстаки";
                        g.metric = MetricKind.Wealth;
                        break;

                    case GoalId.DefensePrep:
                        g.raw = U.Clamp01(w.baseRisk * 0.75f + (w.turrets == 0 ? 0.15f : 0f) + (w.wealth > 20000f ? 0.1f : 0f));
                        g.why = "риск рейда " + U.Pct(w.baseRisk) + ", с прошлого " + U.F(w.daysSinceThreat) + " дн., турелей " + w.turrets;
                        g.action = "турели/ловушки/стены, обучение защите";
                        g.metric = MetricKind.Defense;
                        break;

                    case GoalId.Recruit:
                        g.raw = w.pop < targetColonists
                            ? U.Clamp01(0.25f + (targetColonists - w.pop) * 0.08f) * (w.foodDays > 6f ? 1f : 0.3f) * (1f - w.threatPressure)
                            : 0f;
                        g.why = "людей " + w.pop + " из " + targetColonists + ", еда " + U.F(w.foodDays) + " дн.";
                        g.action = "принимать пришлых, вербовать пленных, квесты";
                        g.metric = MetricKind.Population;
                        break;

                    case GoalId.Ideology:
                        g.raw = w.ideology ? 0.22f : 0f;
                        g.why = w.ideology ? "идеология активна: здания заветов и ритуалы" : "идеология не подключена";
                        g.action = "достраивать требуемое заветами";
                        g.metric = MetricKind.Mood;
                        break;

                    case GoalId.Clean:
                        g.raw = 0.18f + (w.healthUrgency > 0.2f ? 0.12f : 0f);
                        g.why = "грязь и мусор влияют на настроение и болезни";
                        g.action = "уборка в домашней зоне";
                        g.metric = MetricKind.Mood;
                        break;

                    case GoalId.Prisoners:
                        g.raw = w.prisoners > 0 ? 0.45f : 0f;
                        g.why = "пленных " + w.prisoners;
                        g.action = "лечение, кормление, вербовка";
                        g.metric = MetricKind.Population;
                        break;

                    case GoalId.Animals:
                        g.raw = w.pop >= 3 ? 0.18f : 0f;
                        g.why = "животные дают еду, ткани и помощь";
                        g.action = "приручение и разведение";
                        g.metric = MetricKind.Food;
                        break;
                }

                g.weight = Knowledge.GoalWeight(id, w.goalCtx, DefaultWeight(id));
                g.weight *= Learning.GoalMultiplier(MetaFeature(id));
                g.weight *= 1f + Knowledge.LessonBias(id, w.ctx);
                g.utility = g.critical ? Mathf.Max(g.raw, 0.75f) : U.Clamp01(g.raw * g.weight);
                list.Add(g);
            }

            return list.OrderByDescending(x => x.utility).ToList();
        }

        /// <summary>Выбор цели с памятью: текущая цель получает бонус, чтобы не метаться.</summary>
        public static ColonyGoal Pick(List<ColonyGoal> goals, GoalId current, int ticksSinceSwitch)
        {
            if (goals == null || goals.Count == 0) return null;
            ColonyGoal best = goals[0];
            if (current == GoalId.None) return best;
            ColonyGoal cur = goals.FirstOrDefault(g => g.id == current);
            if (cur == null) return best;

            bool urgent = best.critical && best.id != current;
            float hysteresis = ticksSinceSwitch < 2500 ? 0.18f : 0.08f;
            if (!urgent && best.id != current && best.utility < cur.utility + hysteresis) return cur;

            // выбранная критическая цель должна продержаться хотя бы немного
            if (!urgent && cur.critical && cur.utility > 0.55f && ticksSinceSwitch < 5000 && best.utility < 0.9f) return cur;
            return best;
        }
    }
}

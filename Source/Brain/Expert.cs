using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// «Понимание» контента: бот не ищет предметы по названиям, а оценивает их по свойствам —
    /// питательность, лечебная сила, рецепты, стоимость, разблокируемые исследования.
    /// Поэтому работает и с модами, если те используют стандартные поля RimWorld.
    /// </summary>
    public static class DefExpert
    {
        private static Dictionary<ThingDef, float> nutrition;
        private static Dictionary<ThingDef, float> medicine;
        private static Dictionary<ResearchProjectDef, List<ThingDef>> unlocks;
        private static readonly object gate = new object();

        public static Dictionary<ThingDef, float> NutritionMap
        {
            get
            {
                Ensure();
                return nutrition;
            }
        }

        public static Dictionary<ThingDef, float> MedicineMap
        {
            get
            {
                Ensure();
                return medicine;
            }
        }

        private static void Ensure()
        {
            if (nutrition != null && medicine != null) return;
            lock (gate)
            {
                if (nutrition != null && medicine != null) return;
                Dictionary<ThingDef, float> nut = new Dictionary<ThingDef, float>();
                Dictionary<ThingDef, float> med = new Dictionary<ThingDef, float>();
                try
                {
                    StatDef potencyStat = DefDatabase<StatDef>.GetNamedSilentFail("MedicalPotency");
                    foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                    {
                        if (d == null) continue;
                        try
                        {
                            if (d.IsNutritionGivingIngestible && !d.IsDrug && !d.IsCorpse && d.category == ThingCategory.Item)
                            {
                                float n = d.GetStatValueAbstract(StatDefOf.Nutrition);
                                if (n > 0f) nut[d] = n;
                            }
                        }
                        catch { }

                        try
                        {
                            if (potencyStat != null && d.category == ThingCategory.Item && d.BaseMarketValue > 0f)
                            {
                                float potency = d.GetStatValueAbstract(potencyStat);
                                if (potency > 0.01f) med[d] = potency;
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception e) { T.Warn("Каталог еды/лекарств не построен: " + e.Message); }
                nutrition = nut;
                medicine = med;
            }
        }

        /// <summary>Лучшая еда по питательности на единицу стоимости (для торговли и производства).</summary>
        public static string BestFoodHint()
        {
            ThingDef best = null; float bestScore = 0f;
            foreach (var kv in NutritionMap)
            {
                if (kv.Key.BaseMarketValue <= 0.1f) continue;
                float score = kv.Value / Mathf.Max(0.5f, kv.Key.BaseMarketValue);
                if (score > bestScore) { bestScore = score; best = kv.Key; }
            }
            return best == null ? "нет данных" : best.label + " (" + U.F(bestScore, 2) + " пит./серебро)";
        }

        public static string BestMedicineHint()
        {
            ThingDef best = null; float bestP = 0f;
            foreach (var kv in MedicineMap)
                if (kv.Value > bestP) { bestP = kv.Value; best = kv.Key; }
            return best == null ? "лекарств нет" : best.label + " (сила " + U.F(bestP, 2) + ")";
        }

        // ------------------------------------------------------------------ исследования
        private static void EnsureUnlocks()
        {
            if (unlocks != null) return;
            Dictionary<ResearchProjectDef, List<ThingDef>> map = new Dictionary<ResearchProjectDef, List<ThingDef>>();
            try
            {
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (d == null || d.researchPrerequisites == null) continue;
                    foreach (ResearchProjectDef p in d.researchPrerequisites)
                    {
                        if (p == null) continue;
                        List<ThingDef> list;
                        if (!map.TryGetValue(p, out list)) { list = new List<ThingDef>(); map[p] = list; }
                        list.Add(d);
                    }
                }
            }
            catch (Exception e) { T.Warn("Дерево исследований не разобрано полностью: " + e.Message); }
            unlocks = map;
        }

        public static int UnlockCount(ResearchProjectDef p)
        {
            EnsureUnlocks();
            List<ThingDef> list;
            return p != null && unlocks.TryGetValue(p, out list) ? list.Count : 0;
        }

        private static float CategoryWeight(ThingDef d, ColonyWorld w)
        {
            if (d == null) return 0f;
            float weight = 0.4f;
            try
            {
                if (d.IsNutritionGivingIngestible)
                    weight = 2.4f + Mathf.Clamp(8f - w.foodDays, 0f, 8f) * 0.35f;

                float potency;
                if (MedicineMap.TryGetValue(d, out potency))
                    weight = Mathf.Max(weight, 2.0f + w.healthUrgency * 3.0f);

                bool turret = d.thingClass != null && typeof(Building_Turret).IsAssignableFrom(d.thingClass);
                bool trap = d.thingClass != null && typeof(Building_Trap).IsAssignableFrom(d.thingClass);
                if (turret || trap)
                    weight = Mathf.Max(weight, 2.2f + w.threatPressure * 3.0f);

                if (d.HasComp(typeof(CompPowerPlant)))
                    weight = Mathf.Max(weight, 1.6f + (w.powerDeficit > 0f ? 2.5f : 0f));

                if (d.thingClass != null && typeof(Building_Bed).IsAssignableFrom(d.thingClass))
                    weight = Mathf.Max(weight, 2.0f + (w.beds < w.pop ? 2.0f : 0f));

                object toolsObj = R.Get(d, "tools");
                System.Collections.IList tools = toolsObj as System.Collections.IList;
                if (tools != null && tools.Count > 0)
                    weight = Mathf.Max(weight, 1.4f + w.threatPressure * 2.0f);

                if (d.AllRecipes != null && d.AllRecipes.Count > 0)
                    weight += 0.5f;

                if (d.tradeability == Tradeability.Sellable || d.tradeability == Tradeability.All)
                    weight += Mathf.Clamp01(d.BaseMarketValue / 200f) * (0.5f + Mathf.Clamp01(w.wealthTrend / 400f));
            }
            catch { }
            return weight;
        }

        private static float DefValue(ThingDef d, ColonyWorld w)
        {
            float value = Mathf.Sqrt(Mathf.Max(1f, d.BaseMarketValue)) * 1.4f;
            if (d.building != null) value += 6f;
            if (d.AllRecipes != null) value += d.AllRecipes.Count * 0.8f;
            return value * CategoryWeight(d, w);
        }

        /// <summary>Оценка исследования: сколько полезного оно открывает с учётом текущих нужд колонии.</summary>
        public static float ResearchScore(ResearchProjectDef p, ColonyWorld w, out string why)
        {
            why = "";
            if (p == null || w == null) return 0f;
            try
            {
                EnsureUnlocks();
                List<ThingDef> list;
                if (!unlocks.TryGetValue(p, out list) || list.Count == 0)
                {
                    why = p.LabelCap + ": прямых разблокировок в предметах не найдено (возможно, рецепты/способности)";
                    return 0.35f * 100f / Mathf.Max(100f, p.baseCost);
                }

                float total = 0f;
                List<KeyValuePair<float, ThingDef>> top = new List<KeyValuePair<float, ThingDef>>();
                foreach (ThingDef d in list)
                {
                    float v = DefValue(d, w);
                    total += v;
                    top.Add(new KeyValuePair<float, ThingDef>(v, d));
                }
                top.Sort((a, b) => b.Key.CompareTo(a.Key));
                float cost = Mathf.Max(50f, p.baseCost);
                float score = total / Mathf.Pow(cost, 0.72f) * 100f;

                // ранняя игра: дешёвые проекты важнее
                if (w.stage == Stage.Early) score *= Mathf.Clamp(1.6f - cost / 2500f, 0.7f, 1.5f);

                string names = "";
                for (int i = 0; i < Mathf.Min(3, top.Count); i++)
                    names += (i > 0 ? ", " : "") + top[i].Value.label;
                why = p.LabelCap + ": открывает " + list.Count + " объектов (" + names + (list.Count > 3 ? "…" : "") + "), "
                    + (int)cost + " очков, оценка " + U.F(score, 1);
                return score;
            }
            catch (Exception e)
            {
                why = p.LabelCap + ": оценка не удалась (" + e.GetType().Name + ")";
                return 0f;
            }
        }

        public class ResearchPick
        {
            public ResearchProjectDef project;
            public float score;
            public string why = "";
        }

        public static ResearchPick BestResearch(ColonyWorld w)
        {
            ResearchPick best = new ResearchPick();
            if (w == null) return best;
            try
            {
                ResearchProjectDef current = BotBridge.CurrentResearch();
                foreach (ResearchProjectDef p in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                {
                    if (!ColonyWorld.CanStart(p)) continue;
                    string why;
                    float sc = ResearchScore(p, w, out why);
                    if (current != null && p == current) sc *= 1.25f;         // инерция: не дёргаем проект зря
                    if (sc > best.score) { best.score = sc; best.project = p; best.why = why; }
                }
            }
            catch (Exception e) { T.Warn("Выбор исследования не удался: " + e.Message); }
            return best;
        }

        public static float ResearchScoreOf(ResearchProjectDef p, ColonyWorld w)
        {
            string why;
            return ResearchScore(p, w, out why);
        }
    }

    /// <summary>Мост к основному боту: боевая сила, исследования, настройки. Всё через reflection.</summary>
    public static class BotBridge
    {
        private static MethodInfo pawnValue, colonyValue;
        private static bool pawnResolved, colonyResolved;

        public static bool BotPresent { get { return T.FindType("RimWorldBot.BotGameComponent") != null; } }

        public static float PawnCombatValue(Pawn p)
        {
            if (p == null) return 0f;
            try
            {
                if (!pawnResolved)
                {
                    pawnResolved = true;
                    Type t = T.FindType("RimWorldBot.BotCompat");
                    if (t != null) pawnValue = t.GetMethod("PawnCombatValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (pawnValue != null)
                {
                    object r = pawnValue.Invoke(null, new object[] { p });
                    if (r is float) return Mathf.Max(0.1f, (float)r);
                }
            }
            catch { }
            float fallback = 1f;
            try
            {
                if (p.RaceProps != null) fallback = p.RaceProps.Humanlike ? 1.2f : 0.6f;
                if (p.equipment != null && p.equipment.Primary != null) fallback += 0.8f;
                if (p.Downed) fallback *= 0.2f;
            }
            catch { }
            return fallback;
        }

        public static float ColonyCombatValue(Map map)
        {
            try
            {
                if (!colonyResolved)
                {
                    colonyResolved = true;
                    Type t = T.FindType("RimWorldBot.BotCompat");
                    if (t != null) colonyValue = t.GetMethod("ColonyCombatValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (colonyValue != null)
                {
                    object r = colonyValue.Invoke(null, new object[] { map });
                    if (r is float) return Mathf.Max(0f, (float)r);
                }
            }
            catch { }
            return 0f;
        }

        public static ResearchProjectDef CurrentResearch()
        {
            try { return R.Get(Find.ResearchManager, "currentProj") as ResearchProjectDef; }
            catch { return null; }
        }

        public static bool SetResearch(ResearchProjectDef p)
        {
            if (p == null) return false;
            try
            {
                object rm = Find.ResearchManager;
                R.Call(rm, "SetCurrentProject", p);
                return ReferenceEquals(R.Get(rm, "currentProj"), p);
            }
            catch { return false; }
        }

        public static bool SetBotSetting(string field, object value)
        {
            return BotSettingsAccess.Set(field, value);
        }

        public static object GetBotSetting(string field)
        {
            return BotSettingsAccess.Get(field);
        }

        /// <summary>Настройка бота, которой управляет человек: её нельзя менять автоматически.</summary>
        public static bool BotOwns(string field)
        {
            return false;
        }
    }
}

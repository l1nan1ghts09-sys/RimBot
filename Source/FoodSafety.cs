using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Учёт порчи еды: сколько питания реально успеют съесть до истечения срока,
    /// сколько сгниёт, где еда лежит и что делать с излишками.
    /// </summary>
    public static class FoodSafety
    {
        public class Entry
        {
            public Thing thing;
            public float nutrition;
            public float rotDays;
            public float eaten;
            public float spoiled;
            public bool forbidden;
            public bool rottable;
        }

        public class Report
        {
            public float totalNutrition, safeNutrition, spoilNutrition;
            public float dailyNeed, daysFood, safeDays, firstRotDays, spoilPct;
            public int stacks, atRiskStacks;
            public string line = "";
        }

        private static int next;
        private static Map lastMap;
        private static Report last = new Report();

        public static Report Last { get { return last; } }

        public static void ResetRuntime()
        {
            next = 0;
            lastMap = null;
            last = new Report();
        }

        public static void Run(Map map, int tick)
        {
            if (map == null || tick < next) return;
            next = tick + 6000;
            Report r = Analyze(map);
            lastMap = map;
            last = r;
            if (r.totalNutrition < 0.1f) return;

            // Если порчи почти нет — не тратим решения на шум.
            if (r.spoilPct < 0.03f && r.safeDays >= r.daysFood * 0.95f) return;

            float benefit = U.Clamp01(r.spoilPct * 1.4f + (r.safeDays < 3f ? 0.20f : 0f));
            if (!Utility.WeighOnly("food_spoil", "спасти еду от порчи", benefit, 0.06f, 0.02f, 1f,
                "еду съедят до порчи, а излишки уйдут в долгий запас",
                "часть еды сгниёт, и колония потеряет питательность")) return;

            int changed = 0;
            foreach (Entry e in Entries(map, r))
            {
                if (!e.forbidden || e.thing == null || e.thing.Destroyed) continue;
                var f = e.thing.TryGetComp<CompForbiddable>();
                if (f == null || !f.Forbidden) continue;
                f.Forbidden = false;
                changed++;
            }

            if (EnsurePreservation(map, r)) changed++;
            Storage.EnsureFood(map);
            if (changed > 0)
                Mind.Think("еда", "Порча еды: " + r.line + " Включил доступ к еде" + (changed > 0 ? " и поставил заготовку долгого хранения." : "."));
        }

        public static string Line(Map map)
        {
            if (map == null) return "Порча еды: карта не загружена.";
            if (lastMap != map) { last = Analyze(map); lastMap = map; }
            return last.line;
        }

        public static float SafeDays(Map map)
        {
            if (map == null) return 0f;
            if (lastMap != map) { last = Analyze(map); lastMap = map; }
            return last.safeDays;
        }

        public static Report Analyze(Map map)
        {
            var report = new Report();
            if (map == null) return report;
            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            report.dailyNeed = pop * 2.6f;
            List<Entry> list = Entries(map, report);
            float total = 0f, consumed = 0f, first = 9999f;
            int atRisk = 0;
            foreach (Entry e in list)
            {
                total += e.nutrition;
                if (e.rottable && e.rotDays < first) first = e.rotDays;
            }

            // Сначала едим то, что портится раньше. К сроку каждой партии
            // успеваем съесть только то, что колония физически потребляет за это время.
            list.Sort((a, b) => a.rotDays.CompareTo(b.rotDays));
            foreach (Entry e in list)
            {
                if (e.forbidden || e.nutrition <= 0f)
                {
                    e.eaten = 0f;
                    e.spoiled = e.nutrition;
                }
                else
                {
                    float budget = report.dailyNeed * Mathf.Max(0f, e.rotDays) - consumed;
                    e.eaten = Mathf.Clamp(budget, 0f, e.nutrition);
                    e.spoiled = Mathf.Max(0f, e.nutrition - e.eaten);
                    consumed += e.eaten;
                }
                if (e.spoiled > 0.01f) atRisk++;
            }

            report.totalNutrition = total;
            report.safeNutrition = consumed;
            report.spoilNutrition = Mathf.Max(0f, total - consumed);
            report.daysFood = total / Mathf.Max(1f, report.dailyNeed);
            report.safeDays = consumed / Mathf.Max(1f, report.dailyNeed);
            report.firstRotDays = first;
            report.spoilPct = total > 0f ? report.spoilNutrition / total : 0f;
            report.stacks = list.Count;
            report.atRiskStacks = atRisk;
            report.line = "Еда: " + U.F(total, 1) + " пит. (" + U.F(report.daysFood, 1) + " дн.); "
                + "до порчи успеет съесть " + U.F(report.safeDays, 1) + " дн., сгниёт " + U.F(report.spoilNutrition, 1)
                + " пит. (" + U.Pct(report.spoilPct) + "). "
                + (first >= 9998f ? "Скоропортящейся еды нет." : "Первый срок порчи: " + U.F(first, 1) + " дн.");
            return report;
        }

        private static List<Entry> Entries(Map map, Report report)
        {
            List<Entry> list = new List<Entry>();
            Dictionary<ThingDef, float> nut = DefExpert.NutritionMap;
            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (t == null || t.def == null || t.Destroyed || !t.Spawned) continue;
                    float n;
                    if (!nut.TryGetValue(t.def, out n) || n <= 0f) continue;
                    if (t.stackCount <= 0) continue;

                    Entry e = new Entry { thing = t, nutrition = n * t.stackCount };
                    e.forbidden = t.IsForbidden(Faction.OfPlayer);
                    CompRottable rot = t.TryGetComp<CompRottable>();
                    e.rottable = rot != null;
                    if (rot == null || rot.Stage == RotStage.Dessicated)
                    {
                        e.rotDays = 9999f;
                    }
                    else if (rot.Stage == RotStage.Rotting)
                    {
                        e.rotDays = 0f;
                    }
                    else
                    {
                        try { e.rotDays = Mathf.Clamp(rot.TicksUntilRotAtCurrentTemp / 60000f, 0f, 9999f); }
                        catch { e.rotDays = 0f; }
                    }
                    list.Add(e);
                }
            }
            catch (Exception ex) { T.Warn("Учёт порчи еды: " + ex.Message); }
            return list;
        }

        /// <summary>Долгий запас: пеммикан или упакованные пайки, если простая еда сгниёт.</summary>
        private static bool EnsurePreservation(Map map, Report r)
        {
            if (r.spoilNutrition < r.dailyNeed * 0.5f && r.firstRotDays > 5f) return false;

            RecipeDef best = null;
            Building_WorkTable bestTable = null;
            float bestScore = -1f;
            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            try
            {
                foreach (Building_WorkTable table in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
                {
                    if (table == null || table.BillStack == null || table.def.AllRecipes == null) continue;
                    if (table.BillStack.Count >= (TradeEmpireMod.S != null ? TradeEmpireMod.S.maxBillsPerTable : 8)) continue;
                    foreach (RecipeDef recipe in table.def.AllRecipes)
                    {
                        if (recipe == null || !recipe.AvailableNow || recipe.products == null || recipe.products.Count == 0) continue;
                        ThingDef p = recipe.products[0].thingDef;
                        if (p == null) continue;
                        float nutrition;
                        if (!DefExpert.NutritionMap.TryGetValue(p, out nutrition) || nutrition <= 0f) continue;
                        CompProperties_Rottable props = p.GetCompProperties<CompProperties_Rottable>();
                        float days = props != null ? props.daysToRotStart : 999f;
                        if (days < 10f) continue;
                        if (table.BillStack.Bills.Any(b => b.recipe == recipe)) continue;
                        float score = days * nutrition * Mathf.Max(1, recipe.products[0].count);
                        if (score > bestScore) { bestScore = score; best = recipe; bestTable = table; }
                    }
                }
                if (best == null || bestTable == null) return false;
                Bill_Production bill = best.MakeNewBill() as Bill_Production;
                if (bill == null) return false;
                bill.repeatMode = BillRepeatModeDefOf.TargetCount;
                bill.targetCount = Math.Max(4, pop * 3);
                bill.pauseWhenSatisfied = true;
                bill.unpauseWhenYouHave = Math.Max(2, bill.targetCount * 3 / 4);
                bestTable.BillStack.AddBill(bill);
                Mind.Think("еда", "Ставлю «" + best.LabelCap + "» на «" + bestTable.def.label
                    + "»: еда с малым сроком не успевает съедаться, нужен долгий запас.");
                return true;
            }
            catch (Exception ex) { T.Warn("Долгий запас еды: " + ex.Message); return false; }
        }
    }
}

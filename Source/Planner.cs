using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Мод-независимый планировщик производства: для каждого верстака считает прибыль на единицу работы
    /// у всех доступных рецептов (в т.ч. из модов) и держит bills "до N штук" на лучших.
    /// </summary>
    public static class Planner
    {
        public static Dictionary<ThingDef, int> IngredientWanted = new Dictionary<ThingDef, int>();
        private static readonly string[] Protected = { "Silver", "Gold", "Plasteel", "Uranium", "Jade" };

        public static void ResetRuntime() { IngredientWanted.Clear(); }

        public class Scored
        {
            public RecipeDef recipe; public float density; public float unitValue; public ThingDef product;
            public bool feasible; public ThingDef ingredient; public int ingredientPerUnit;
        }

        public static void Run(Map map)
        {
            var s = TradeEmpireMod.S;
            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count == 0) return;
            var wanted = new Dictionary<ThingDef, int>();

            foreach (Building_WorkTable table in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().ToList())
            {
                if (!table.Spawned || table.def.AllRecipes == null || table.BillStack == null) continue;
                var power = table.GetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn) continue;
                var ranked = Rank(table.def, map, colonists);
                PlanTable(table, ranked, map, wanted);
            }
            IngredientWanted = wanted;
        }

        public static List<Scored> Rank(ThingDef tableDef, Map map, List<Pawn> colonists)
        {
            var res = new List<Scored>();
            foreach (RecipeDef r in tableDef.AllRecipes)
            {
                Scored sc = Score(r, map, colonists);
                if (sc != null) res.Add(sc);
            }
            return res.OrderByDescending(x => x.feasible ? 1 : 0).ThenByDescending(x => x.density).ToList();
        }

        private static Scored Score(RecipeDef r, Map map, List<Pawn> colonists)
        {
            if (!r.AvailableNow || r.products == null || r.products.Count != 1 || r.IsSurgery) return null;
            if (r.specialProducts != null && r.specialProducts.Count > 0) return null;
            ThingDef p = r.products[0].thingDef;
            if (p == null || p.BaseMarketValue <= 0.5f) return null;
            if (p.tradeability != Tradeability.Sellable && p.tradeability != Tradeability.All) return null;
            if (!(p.category == ThingCategory.Item || (p.category == ThingCategory.Building && p.Minifiable))) return null;
            if (p.IsNutritionGivingIngestible && !p.IsDrug) return null;   // еду для колонии ведёт основной бот
            int outCount = Math.Max(1, r.products[0].count);

            // Лучший исполнитель
            float bestSkill = -1f;
            foreach (Pawn c in colonists)
            {
                if (c.Downed || c.skills == null) continue;
                if (r.requiredGiverWorkType != null && c.WorkTypeIsDisabled(r.requiredGiverWorkType)) continue;
                if (!r.PawnSatisfiesSkillRequirements(c)) continue;
                float lv = r.workSkill != null ? c.skills.GetSkill(r.workSkill).Level : 8f;
                if (lv > bestSkill) bestSkill = lv;
            }
            if (bestSkill < 0f) return null;

            // Ингредиенты: самый дешёвый доступный вариант
            float ingValue = 0; ThingDef mainIng = null; int mainPerUnit = 0; bool feasible = true; ThingDef stuff = null;
            foreach (IngredientCount ic in r.ingredients)
            {
                float need = ic.GetBaseCount();
                ThingDef cheapest = null; float cv = float.MaxValue; bool anyAvail = false;
                foreach (ThingDef d in ic.filter.AllowedThingDefs)
                {
                    if (d.BaseMarketValue <= 0f) continue;
                    if (IsProtected(d) && p.MadeFromStuff) continue;
                    bool avail = T.Count(map, d) >= need * 2;
                    if (avail && d.BaseMarketValue < cv) { cv = d.BaseMarketValue; cheapest = d; anyAvail = true; }
                    else if (!anyAvail && d.BaseMarketValue < cv && !IsProtected(d)) { cv = d.BaseMarketValue; cheapest = d; }
                }
                if (cheapest == null) return null;
                if (!anyAvail) feasible = false;
                ingValue += cv * need;
                if (mainIng == null) { mainIng = cheapest; mainPerUnit = Mathf.CeilToInt(need / outCount); }
                if (p.MadeFromStuff && cheapest.IsStuff && stuff == null) stuff = cheapest;
            }
            if (p.MadeFromStuff && stuff == null) stuff = GenStuff.DefaultStuffFor(p);

            float unit = p.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
            float qualityMul = Mathf.Clamp(0.7f + 0.06f * bestSkill, 0.7f, 1.8f);
            if (p.HasComp(typeof(CompQuality)) == false) qualityMul = 1f;
            float revenue = unit * qualityMul * outCount;
            float profit = revenue - ingValue;
            if (profit <= 0f) return null;
            float work = Math.Max(1f, r.workAmount > 0f ? r.workAmount : 100f);
            // спрос из фактических продаж: то, что реально покупают, выгоднее производить
            float demand = Ledger.Demand(p.defName);
            float demandMul = 1f + Mathf.Clamp01(demand / 30f) * 0.8f;
            return new Scored
            {
                recipe = r, product = p, unitValue = unit * qualityMul, density = profit / work * 1000f * demandMul,
                feasible = feasible, ingredient = mainIng, ingredientPerUnit = mainPerUnit
            };
        }

        private static bool IsProtected(ThingDef d) { return Protected.Contains(d.defName); }

        private static void PlanTable(Building_WorkTable table, List<Scored> ranked, Map map, Dictionary<ThingDef, int> wanted)
        {
            var s = TradeEmpireMod.S;
            int made = 0;
            foreach (Scored sc in ranked)
            {
                if (made >= s.recipesPerTable) break;
                made++;
                float demand = Ledger.Demand(sc.product.defName);
                int tc = Mathf.Clamp((int)(s.stockTargetValue / Math.Max(1f, sc.unitValue) * (1f + Mathf.Clamp01(demand / 30f))), 2, 80);

                // что докупать/не продавать
                if (sc.ingredient != null)
                {
                    int need = sc.ingredientPerUnit * Math.Min(tc, 10);
                    int cur; wanted.TryGetValue(sc.ingredient, out cur);
                    wanted[sc.ingredient] = Math.Max(cur, need);
                }
                if (!sc.feasible) continue;

                Bill_Production existing = table.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b => b.recipe == sc.recipe);
                if (existing != null)
                {
                    if (existing.repeatMode == BillRepeatModeDefOf.TargetCount) existing.targetCount = tc;
                    continue;
                }
                if (table.BillStack.Count >= s.maxBillsPerTable) break;
                var bill = sc.recipe.MakeNewBill() as Bill_Production;
                if (bill == null) continue;
                bill.repeatMode = BillRepeatModeDefOf.TargetCount;
                bill.targetCount = tc;
                bill.pauseWhenSatisfied = true;
                bill.unpauseWhenYouHave = Math.Max(1, tc * 3 / 4);
                if (sc.product.MadeFromStuff)
                    foreach (string n in Protected)
                    {
                        ThingDef d = T.Def(n);
                        if (d != null) bill.ingredientFilter.SetAllow(d, false);
                    }
                table.BillStack.AddBill(bill);
                T.Info("Новое задание: " + sc.recipe.label + " x" + tc + " на «" + table.LabelCap + "» (прибыль/работу: " + sc.density.ToString("0.0") + ")");
            }
        }
    }
}

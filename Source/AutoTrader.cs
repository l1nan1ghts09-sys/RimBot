using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    public static class AutoTrader
    {
        private class Line { public Tradeable tr; public int count; public float unit; }

        public static Pawn BestNegotiator(Map map)
        {
            Pawn best = null; float bestScore = float.MinValue;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Downed || p.InMentalState || p.IsPrisoner) continue;
                if (p.health == null || !p.health.capacities.CapableOf(PawnCapacityDefOf.Talking)) continue;
                float sc = p.GetStatValue(StatDefOf.TradePriceImprovement, true);
                if (sc > bestScore) { bestScore = sc; best = p; }
            }
            return best;
        }

        /// <summary>Сессия с торговцем без UI: набор сделки -> исполнение -> закрытие.</summary>
        public static bool TradeWith(ITrader trader, Pawn negotiator, Map map)
        {
            if (TradeSession.Active) return false;
            bool ok = false;
            TradeSession.SetupWith(trader, negotiator, false);
            try { ok = ExecuteCurrent(map); }
            finally { TradeSession.Close(); }
            return ok;
        }

        public static void HandleOpenDialog(ref int seenTick)
        {
            if (!TradeSession.Active) { seenTick = -1; return; }
            var win = Find.WindowStack.WindowOfType<Dialog_Trade>();
            if (win == null) return;
            int now = Find.TickManager.TicksGame;
            if (seenTick < 0) { seenTick = now; return; }
            if (now - seenTick < 90) return;   // даём человеку шанс, если он играет сам
            seenTick = -1;
            Map map = TradeSession.playerNegotiator != null && TradeSession.playerNegotiator.Map != null
                ? TradeSession.playerNegotiator.Map : Find.CurrentMap;
            ExecuteCurrent(map);
            win.Close(true);
        }

        private static bool ExecuteCurrent(Map map)
        {
            var s = TradeEmpireMod.S;
            TradeDeal deal = TradeSession.deal;
            if (deal == null || map == null) return false;

            List<Tradeable> all = deal.AllTradeables.Where(t => !t.IsCurrency && !(t.AnyThing is Pawn)).ToList();
            Tradeable cur = deal.CurrencyTradeable;
            if (cur == null) return false;
            float playerSilver = cur.CountHeldBy(Transactor.Colony);
            float traderSilver = cur.CountHeldBy(Transactor.Trader);

            // Определяем знак «продажа» эмпирически (ActionToDo после тестового ForceTo).
            int sellSign = DetectSellSign(all);

            var sells = new List<Line>();
            var buys = new List<Line>();
            Policy.Prepare(map);

            foreach (Tradeable tr in all)
            {
                ThingDef def = tr.ThingDef;
                if (def == null) continue;
                int have = tr.CountHeldBy(Transactor.Colony);
                if (have > 0 && tr.TraderWillTrade && !Policy.NeverSell(tr))
                {
                    int sellable = have - Policy.Reserve(def, map);
                    float p = tr.GetPriceFor(TradeAction.PlayerSells);
                    if (sellable > 0 && p > 0.01f && p >= def.BaseMarketValue * s.minSellRatio)
                        sells.Add(new Line { tr = tr, count = sellable, unit = p });
                }
                int stock = tr.CountHeldBy(Transactor.Trader);
                if (stock > 0)
                {
                    int want = Policy.Wanted(def, map);
                    float p = tr.GetPriceFor(TradeAction.PlayerBuys);
                    if (want > 0 && p > 0.01f && p <= def.BaseMarketValue * s.maxBuyRatio)
                        buys.Add(new Line { tr = tr, count = Math.Min(want, stock), unit = p });
                }
            }

            float sellAll = sells.Sum(l => l.count * l.unit);
            // Покупки: из резерва серебра + то, что трейдер реально может заплатить за наши товары.
            float budget = playerSilver - s.silverReserve + Math.Min(sellAll, traderSilver);
            var chosenBuys = new List<Line>(); float buyValue = 0;
            foreach (Line b in buys.OrderBy(b => b.unit))
            {
                int n = Math.Min(b.count, (int)((budget - buyValue) / b.unit));
                if (n <= 0) continue;
                b.count = n; chosenBuys.Add(b); buyValue += n * b.unit;
            }

            // Продажи ограничены серебром трейдера (+ покупки, т.к. это бартер).
            float cap = traderSilver + buyValue;
            var chosenSells = new List<Line>(); float sellValue = 0;
            foreach (Line l in sells.OrderByDescending(x => x.unit))
            {
                int n = Math.Min(l.count, (int)((cap - sellValue) / l.unit));
                if (n <= 0) continue;
                l.count = n; chosenSells.Add(l); sellValue += n * l.unit;
            }

            if (sellValue < s.minDealValue && chosenBuys.Count == 0) return false;
            if (sellValue < buyValue * 0.2f && sellValue < s.minDealValue) return false;

            foreach (Line l in chosenSells) Force(l.tr, sellSign * l.count);
            foreach (Line b in chosenBuys) Force(b.tr, -sellSign * b.count);
            deal.UpdateCurrencyCount();

            // Проверку серебра оставляем TryExecute: в 1.6 у TradeDeal больше нет публичных Does*EnoughSilver.
            bool traded;
            bool ok = deal.TryExecute(out traded);
            if (ok && traded)
            {
                Ledger.C?.RecordDeal(sellValue, buyValue);
                foreach (Line l in chosenSells) Ledger.RecordSale(l.tr.ThingDef, l.count);
                T.Info("Сделка: продано на " + (int)sellValue + ", куплено на " + (int)buyValue
                    + " (" + chosenSells.Count + " поз. / " + chosenBuys.Count + " поз.). " + Ledger.Summary());
                return true;
            }
            deal.Reset();
            return false;
        }

        private static void Force(Tradeable tr, int signed)
        {
            int min = tr.GetMinimumToTransfer(), max = tr.GetMaximumToTransfer();
            tr.ForceTo(Mathf.Clamp(signed, min, max));
        }

        private static int DetectSellSign(List<Tradeable> all)
        {
            foreach (Tradeable tr in all)
            {
                if (tr.CountHeldBy(Transactor.Colony) <= 0) continue;
                int min = tr.GetMinimumToTransfer(), max = tr.GetMaximumToTransfer();
                tr.ForceTo(Mathf.Clamp(1, min, max));
                var act = tr.ActionToDo;
                tr.ForceTo(0);
                if (act == TradeAction.PlayerSells) return 1;
                if (act == TradeAction.PlayerBuys) return -1;
            }
            return 1;
        }
    }

    /// <summary>Что продавать, что держать в запасе, что докупать.</summary>
    public static class Policy
    {
        /// <summary>Во время продовольственного кризиса еду не продаём совсем.</summary>
        public static bool HoldFood;

        private static readonly string[] Never = { "techprint", "relic", "archon", "mechanoid", "persona", "quest", "ancient", "grammophone" };
        private static float foodKeepFraction = 1f;
        private static int colonists;

        public static void Prepare(Map map)
        {
            colonists = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            int days = T.BotInt("minFoodDays", 12);
            float need = colonists * 2.6f * Math.Max(days, 8);
            float stock = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (IsFood(d)) stock += T.Count(map, d) * d.GetStatValueAbstract(StatDefOf.Nutrition);
            foodKeepFraction = stock <= 0 ? 1f : Mathf.Clamp01(need * 1.6f / stock);
        }

        private static bool IsFood(ThingDef d)
        {
            return d.IsNutritionGivingIngestible && !d.IsDrug && !d.IsCorpse && d.category == ThingCategory.Item
                && d.ingestible != null && d.ingestible.preferability != FoodPreferability.Undefined
                && d.ingestible.preferability != FoodPreferability.NeverForNutrition;
        }

        public static bool NeverSell(Tradeable tr)
        {
            string n = tr.ThingDef.defName.ToLowerInvariant();
            foreach (string x in Never) if (n.Contains(x)) return true;
            var twc = tr.AnyThing as ThingWithComps;
            if (twc != null)
                foreach (ThingComp c in twc.AllComps)
                {
                    string cn = c.GetType().Name;
                    if (cn.Contains("Relic") || cn.Contains("Quest")) return true;
                }
            return false;
        }

        public static int Reserve(ThingDef d, Map map)
        {
            int keep = 0;
            if (d == ThingDefOf.Steel) keep = Math.Max(T.BotInt("minSteelCount", 300), 300);
            else if (d == ThingDefOf.ComponentIndustrial) keep = Math.Max(T.BotInt("minComponentsCount", 20), 20);
            else if (d == ThingDefOf.Plasteel) keep = 100;
            else if (d == ThingDefOf.WoodLog) keep = 250;
            else if (d == ThingDefOf.MedicineIndustrial || d == ThingDefOf.MedicineHerbal || d == ThingDefOf.MedicineUltratech)
                keep = Math.Max(T.BotInt("minMedicineCount", 10), 12);
            else if (d.IsDrug) keep = 6;
            else if (IsFood(d))
            {
                int have = T.Count(map, d);
                keep = HoldFood ? have : Mathf.CeilToInt(have * foodKeepFraction);
            }
            int ing;
            if (Planner.IngredientWanted.TryGetValue(d, out ing)) keep = Math.Max(keep, ing);
            return keep;
        }

        /// <summary>Сколько докупить: дефицит базовых ресурсов + ингредиенты производства.</summary>
        public static int Wanted(ThingDef d, Map map)
        {
            int have = T.Count(map, d);
            int target = 0;
            if (d == ThingDefOf.Steel) target = Math.Max(T.BotInt("minSteelCount", 300), 300) + 100;
            else if (d == ThingDefOf.ComponentIndustrial) target = Math.Max(T.BotInt("minComponentsCount", 20), 20) + 10;
            else if (d == ThingDefOf.MedicineIndustrial) target = Math.Max(T.BotInt("minMedicineCount", 10), 12) + 6;
            int ing;
            if (Planner.IngredientWanted.TryGetValue(d, out ing)) target = Math.Max(target, ing);
            return Math.Max(0, target - have);
        }
    }
}

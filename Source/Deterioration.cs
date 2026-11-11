using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Учёт ветшания вещей на улице: сколько HP теряется в день, сколько осталось
    /// до разрушения и какую ценность нужно срочно убрать под крышу.
    /// </summary>
    public static class Deterioration
    {
        public class Risk
        {
            public Thing thing;
            public float rate, hpPct, daysLeft, value;
        }

        public class Report
        {
            public int count;
            public float value, worstDays, worstHpPct;
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
            next = tick + 9000;
            Report r = Analyze(map);
            lastMap = map;
            last = r;
            if (r.count == 0) return;

            float wealth = 1000f;
            try { wealth = map.wealthWatcher.WealthTotal; } catch { }
            float benefit = U.Clamp01(r.value / Mathf.Max(1000f, wealth * 0.15f) * 0.85f
                + (r.worstDays < 5f ? 0.15f : 0f));
            if (!Utility.WeighOnly("deterioration", "убрать вещи с улицы под крышу", benefit, 0.05f, 0.02f, 1f,
                "вещи перестанут терять прочность и не разрушатся",
                "дождь и погода продолжат уничтожать снаряжение и товары")) return;

            int moved = 0;
            foreach (Risk x in Risks(map))
            {
                if (x.thing == null || x.thing.Destroyed) continue;
                var f = x.thing.TryGetComp<CompForbiddable>();
                if (f != null && f.Forbidden) { f.Forbidden = false; moved++; }
            }

            Zone_Stockpile zone = Storage.EnsureSheltered(map);
            if (zone == null)
            {
                // Крытого склада ещё нет — просим жильё, оно же даст крышу.
                Mind.Think("хранение", "Ветшание: " + r.line + " Крытого склада нет — запускаю постройку помещения.");
                Vanilla.Shelter(map);
                return;
            }

            Mind.Think("хранение", "Ветшание: " + r.line
                + " Включаю доступ к вещам и перевожу их в крытый склад «" + zone.label + "» (приоритет Critical).");
        }

        public static string Line(Map map)
        {
            if (map == null) return "Ветшание: карта не загружена.";
            if (lastMap != map) { last = Analyze(map); lastMap = map; }
            return last.line;
        }

        public static Report Analyze(Map map)
        {
            Report r = new Report();
            r.worstDays = 9999f;
            if (map == null) return r;
            List<Risk> risks = Risks(map);
            r.count = risks.Count;
            foreach (Risk x in risks)
            {
                r.value += x.value;
                if (x.daysLeft < r.worstDays) r.worstDays = x.daysLeft;
                if (x.hpPct < r.worstHpPct || r.worstHpPct <= 0f) r.worstHpPct = x.hpPct;
            }
            if (r.count == 0)
                r.line = "Ветшание: все вещи под крышей или не теряют прочность.";
            else
                r.line = "Ветшание: " + r.count + " вещей на улице, худшая прочность " + U.Pct(r.worstHpPct)
                    + ", примерно " + U.F(r.worstDays, 1) + " дн. до разрушения, под угрозой "
                    + (int)r.value + " сер.";
            return r;
        }

        private static List<Risk> Risks(Map map)
        {
            List<Risk> list = new List<Risk>();
            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (t == null || t.def == null || t.Destroyed || !t.Spawned) continue;
                    if (t.def.category != ThingCategory.Item || !t.def.useHitPoints) continue;
                    if (t.Position.Roofed(map)) continue;
                    float rate = 0f;
                    try { rate = t.GetStatValue(StatDefOf.DeteriorationRate, true); } catch { }
                    if (rate <= 0.001f) continue;
                    if (t.MaxHitPoints <= 0) continue;
                    float value = 0f;
                    try { value = t.MarketValue * t.stackCount; } catch { }
                    float days = t.HitPoints / rate;
                    list.Add(new Risk
                    {
                        thing = t, rate = rate, hpPct = t.HitPoints / (float)t.MaxHitPoints,
                        daysLeft = Mathf.Clamp(days, 0f, 9999f), value = value
                    });
                }
            }
            catch (Exception ex) { T.Warn("Учёт ветшания: " + ex.Message); }
            return list;
        }
    }
}

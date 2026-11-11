using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Взвешивание решений: польза, цена, риск и срочность. Перед действием бот
    /// записывает, что будет, если сделать, и что будет, если не сделать.
    /// </summary>
    public static class Utility
    {
        public class Decision
        {
            public int tick;
            public string id = "";
            public string title = "";
            public float benefit, cost, risk, urgency, score;
            public bool accepted;
            public string ifDo = "";
            public string ifSkip = "";
            public string Line
            {
                get
                {
                    return (accepted ? "ДЕЛАЮ" : "НЕ ДЕЛАЮ") + " «" + title + "» (польза " + U.Pct(U.Clamp01(benefit))
                        + ", цена " + U.Pct(U.Clamp01(cost)) + ", риск " + U.Pct(U.Clamp01(risk))
                        + ", срочность " + U.F(urgency) + ", счёт " + U.F(score, 2) + "). "
                        + (accepted ? "Если сделать: " + ifDo : "Если не сделать: " + ifSkip);
                }
            }
        }

        private static readonly List<Decision> recent = new List<Decision>();
        private static int lastLogTick = -99999;
        private static string lastTitle = "";

        public static List<Decision> Recent { get { return recent; } }
        public static string LastLine { get { return recent.Count > 0 ? recent[recent.Count - 1].Line : "Решений пока нет."; } }

        public static void ResetRuntime()
        {
            recent.Clear();
            lastLogTick = -99999;
            lastTitle = "";
        }

        public static float Score(float benefit, float cost, float risk, float urgency)
        {
            return benefit * Mathf.Clamp(urgency, 0.1f, 2f) - cost - risk;
        }

        /// <summary>Взвесить и, если польза перевешивает, выполнить действие.</summary>
        public static bool Weigh(string id, string title, float benefit, float cost, float risk, float urgency,
            string ifDo, string ifSkip, Action act)
        {
            bool ok = WeighOnly(id, title, benefit, cost, risk, urgency, ifDo, ifSkip);
            if (ok && act != null)
            {
                try { act(); }
                catch (Exception e) { T.Warn("Действие «" + title + "»: " + e.Message); }
            }
            return ok;
        }

        /// <summary>Только решение, без выполнения — для подсистем, которые сами решают, что делать.</summary>
        public static bool WeighOnly(string id, string title, float benefit, float cost, float risk, float urgency,
            string ifDo, string ifSkip)
        {
            float score = Score(benefit, cost, risk, urgency);
            bool accept = score >= 0.05f;
            var d = new Decision
            {
                tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                id = id, title = title, benefit = U.Clamp01(benefit), cost = U.Clamp01(cost),
                risk = U.Clamp01(risk), urgency = Mathf.Clamp(urgency, 0.1f, 2f), score = score,
                accepted = accept, ifDo = ifDo, ifSkip = ifSkip
            };
            recent.Add(d);
            if (recent.Count > 200) recent.RemoveRange(0, 50);

            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (accept || title != lastTitle || now - lastLogTick > 3000)
            {
                lastLogTick = now;
                lastTitle = title;
                Mind.Think("решение", d.Line);
            }
            return accept;
        }

        public static string Summary(int n)
        {
            if (recent.Count == 0) return "Решений пока нет: бот ждёт карту.";
            var list = recent.Skip(Math.Max(0, recent.Count - n)).ToList();
            return string.Join("\n", list.Select(x => x.Line).ToArray());
        }

        public static int AcceptedCount
        {
            get { return recent.Count(x => x.accepted); }
        }

        // ------------------------------------------------------------ давление обстоятельств

        public static float FoodPressure(Map map)
        {
            try
            {
                BrainComponent b = BrainComponent.Get();
                if (b != null && b.World != null)
                {
                    float d = b.World.foodDays;
                    if (b.World.foodSafeDays > 0f && b.World.foodSafeDays < d) d = b.World.foodSafeDays;
                    return U.Clamp01((12f - d) / 12f);
                }
            }
            catch { }
            return 0.4f;
        }

        public static float SteelPressure(Map map)
        {
            return U.Clamp01(1f - T.Count(map, ThingDefOf.Steel) / 500f);
        }

        public static float ThreatPressure(Map map)
        {
            try
            {
                if (GenHostility.AnyHostileActiveThreatToPlayer(map)) return 1f;
                Defense.Report r = Defense.Last;
                if (r != null) return U.Clamp01(r.difficulty);
            }
            catch { }
            return 0.3f;
        }

        public static float MedicinePressure(Map map)
        {
            int n = T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal);
            return U.Clamp01((12f - n) / 12f);
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Исправление «вечного боя»: BotCompat.HostileCombatValue считает врагами ВСЕХ вражеских пешек на карте
    /// (пленных, спящих, далёких форпостов), из-за чего бот постоянно «отступает». Подменяем результат на реальную угрозу.
    /// Harmony вызывается через reflection — жёсткой зависимости нет.
    /// </summary>
    public static class ThreatFix
    {
        private static bool installed, failedOnce;
        private static int tries;
        private static MethodInfo pcv;
        private static int quietSince = -1;

        public static void Install()
        {
            if (installed || tries >= 5) return;
            tries++;
            var hT = T.FindType("HarmonyLib.Harmony");
            var hmT = T.FindType("HarmonyLib.HarmonyMethod");
            var compat = T.FindType("RimWorldBot.BotCompat");
            var target = compat != null ? compat.GetMethod("HostileCombatValue", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
            pcv = compat != null ? compat.GetMethod("PawnCombatValue", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
            if (hT == null || hmT == null || target == null)
            {
                if (!failedOnce) { failedOnce = true; T.Warn("ThreatFix: Harmony или BotCompat.HostileCombatValue не найдены (попытка " + tries + ")."); }
                return;
            }
            object harmony = Activator.CreateInstance(hT, new object[] { "rimbot.tradeempire.threatfix" });
            MethodInfo post = typeof(ThreatFix).GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public);
            object hm = Activator.CreateInstance(hmT, new object[] { post });
            MethodInfo patch = hT.GetMethods().First(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            patch.Invoke(harmony, new object[] { target, null, hm, null, null });
            installed = true;
            Mind.Think("решение", "Исправлен «вечный бой»: враги теперь считаются только если они активны, не в плену и ближе 60 клеток к колонистам.");
        }

        // Harmony postfix: __result — значение оригинала, __0 — карта.
        public static void Postfix(ref float __result, Map __0)
        {
            try { __result = Real(__0, __result); } catch { }
        }

        private static float Real(Map map, float original)
        {
            if (map == null) return original;
            var cols = map.mapPawns.FreeColonistsSpawned;
            if (cols.Count == 0) return original;
            float sum = 0f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == null || p.Dead || p.Downed || p.Faction == null) continue;
                if (!p.Faction.HostileTo(Faction.OfPlayer)) continue;
                if (p.HostFaction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony) continue;
                if (!GenHostility.IsActiveThreatToPlayer(p)) continue;
                float d = float.MaxValue;
                for (int i = 0; i < cols.Count; i++) d = Math.Min(d, p.Position.DistanceTo(cols[i].Position));
                if (d > 60f) continue;
                float v = 1f;
                if (pcv != null) { object r = pcv.Invoke(null, new object[] { p }); if (r is float) v = (float)r; }
                sum += v * 1.1f;
            }
            return sum;
        }

        /// <summary>Когда угрозы нет — вернуть колонистов к нормальной жизни (бот оставляет им «бегство» и призыв).</summary>
        public static void ReleaseColonists(Map map, int tick)
        {
            if (GenHostility.AnyHostileActiveThreatToPlayer(map)) { quietSince = -1; return; }
            if (quietSince < 0) { quietSince = tick; return; }
            if (tick - quietSince < 600) return;
            int fixedCount = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.playerSettings != null && p.playerSettings.hostilityResponse == HostilityResponseMode.Flee && !p.WorkTagIsDisabled(WorkTags.Violent))
                { p.playerSettings.hostilityResponse = HostilityResponseMode.Attack; fixedCount++; }
                if (p.Drafted && p.drafter != null) { p.drafter.Drafted = false; fixedCount++; }
            }
            if (fixedCount > 0) Mind.Think("решение", "Угрозы нет: снял призыв и режим «бегство» у " + fixedCount + " колонистов, возвращаю к работе.");
        }
    }
}

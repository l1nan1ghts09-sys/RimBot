using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Авто-выбор в диалогах событий, чистка писем, управление скоростью времени.</summary>
    public static class Control
    {
        private static float nextDialog, nextSpeed, seenAt, lastThreat = -100f;
        private static Window seen;

        private static readonly string[] Good = { "accept", "yes", "agree", "welcome", "help", "принять", "принима", "да", "согласен", "согласи", "хорошо", "помочь", "радушн" };
        private static readonly string[] Neutral = { "close", "continue", "ok", "okay", "thanks", "understood", "закрыть", "продолж", "понятно", "ладно", "ок", "спасибо", "дальше" };
        private static readonly string[] Bad = { "reject", "decline", "refuse", "attack", "kill", "execute", "no", "отклон", "отказ", "нет", "напасть", "убить", "казн", "прогнать" };

        public static void OnGUI()
        {
            var s = TradeEmpireMod.S;
            if (s == null || !s.enabled || Event.current == null || Event.current.type != EventType.Repaint) return;
            float now = Time.realtimeSinceStartup;
            if (s.visitorTrade) T.Safe("TradeUI", AutoTrader.OnGUI);
            if (s.autoControl && now >= nextDialog) { nextDialog = now + 1f; T.Safe("Dialogs", HandleDialogs); }
            if (s.autoSpeed && now >= nextSpeed) { nextSpeed = now + 2f; T.Safe("Speed", ApplySpeed); }
        }

        // ---------- диалоги ----------
        private static int Score(DiaOption o)
        {
            string t = ((R.Get(o, "text") as string) ?? "").ToLowerInvariant();
            int sc = 0;
            if (o.disabled) return -100;
            foreach (string w in Good) if (t.Contains(w)) sc += 3;
            foreach (string w in Neutral) if (t.Contains(w)) sc += 1;
            foreach (string w in Bad) if (t.Contains(w)) sc -= 3;
            return sc;
        }

        public static DiaOption Choose(IEnumerable<DiaOption> options)
        {
            DiaOption best = null; int bs = int.MinValue;
            foreach (DiaOption o in options) { int sc = Score(o); if (sc > bs) { bs = sc; best = o; } }
            return best;
        }

        private static void HandleDialogs()
        {
            var win = Find.WindowStack.Windows.OfType<Dialog_NodeTree>().FirstOrDefault();
            if (win == null) { seen = null; return; }
            float now = Time.realtimeSinceStartup;
            if (seen != win) { seen = win; seenAt = now; return; }
            if (now - seenAt < 3f) return;                       // даём человеку шанс ответить самому
            var node = typeof(Dialog_NodeTree).GetField("curNode", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(win) as DiaNode;
            seen = null;
            if (node == null || node.options == null || node.options.Count == 0) { win.Close(true); return; }
            DiaOption pick = Choose(node.options);
            if (pick == null || pick.disabled) { win.Close(true); return; }
            string text = node.text.ToString();
            if (text.Length > 90) text = text.Substring(0, 90) + "…";
            Mind.Think("событие", "Диалог «" + text.Replace("\n", " ") + "» → выбираю «" + (R.Get(pick, "text") as string ?? "?") + "»");
            try
            {
                MethodInfo act = typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (act != null) act.Invoke(pick, null); else win.Close(true);
            }
            catch (Exception e) { Log.Warning("[RimBot] Не удалось нажать вариант диалога: " + e.Message); win.Close(true); }
        }

        // ---------- письма ----------
        public static void CleanLetters()
        {
            int now = Find.TickManager.TicksGame;
            foreach (Letter l in Find.LetterStack.LettersListForReading.ToList())
            {
                if (l.GetType() != typeof(StandardLetter)) continue;       // выбор/квесты не трогаем
                var f = typeof(Letter).GetField("arrivalTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? typeof(Letter).GetField("arrivedTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                int arrived = f != null && f.GetValue(l) is int ? (int)f.GetValue(l) : now;
                if (now - arrived > 30000) Find.LetterStack.RemoveLetter(l);
            }
        }

        // ---------- время ----------
        private static void ApplySpeed()
        {
            var s = TradeEmpireMod.S;
            var tm = Find.TickManager;
            if (tm.Paused) return;                                    // паузу снимает Mind.OnGUI
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            float now = Time.realtimeSinceStartup;
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            bool threat = GenHostility.AnyHostileActiveThreatToPlayer(map)
                || cols.Any(p => p.Downed || p.health.hediffSet.BleedRateTotal > 0.2f || p.InMentalState);
            if (threat) lastThreat = now;

            TimeSpeed target;
            if (threat || now - lastThreat < 4f) target = TimeSpeed.Normal;
            else
            {
                bool allAsleep = cols.Count > 0 && cols.All(p => p.jobs != null && p.jobs.curDriver != null && p.jobs.curDriver.asleep);
                int level = s.fastSpeed ? 3 : Mathf.Clamp(s.calmSpeed, 1, 3);
                if (allAsleep) level = 3;
                target = level == 1 ? TimeSpeed.Normal : level == 2 ? TimeSpeed.Fast : TimeSpeed.Superfast;
            }
            if (tm.CurTimeSpeed != target)
            {
                tm.CurTimeSpeed = target;
                Mind.Think("время", "Скорость → " + target + (target == TimeSpeed.Normal ? " (тревога/потери)" : " (спокойно)"));
            }
        }
    }

    /// <summary>Квесты: принимаем только те, что можно выполнить силами дома (без каравана).</summary>
    public static class Quests
    {
        private static readonly HashSet<int> decided = new HashSet<int>();
        private static readonly string[] Safe = { "Joiner", "Refugee", "Wanderer", "Hospitality", "ThreatReward", "ItemStash", "TradeRequest" };

        /// <summary>Письма-предложения квестов: принимаем безопасные прямо кнопкой Accept, чтобы они не висели в стеке.</summary>
        private static void AcceptLetters(List<Pawn> cols)
        {
            try
            {
                foreach (Letter l in Find.LetterStack.LettersListForReading.ToList())
                {
                    string tn = l.GetType().Name;
                    if (tn.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Quest q = R.Get(l, "quest") as Quest;
                    if (q == null || q.State != QuestState.NotYetAccepted || q.Historical) continue;
                    if (!decided.Add(q.id)) continue;
                    string root = q.root != null ? q.root.defName : "?";
                    bool safe = Safe.Any(w => root.Contains(w));
                    if (!safe)
                    {
                        Mind.Think("квест", "Письмо «" + q.name + "» (" + root + ") не принимаю: нужен караван или риск неизвестен.");
                        continue;
                    }
                    Pawn who = cols.OrderByDescending(p => p.GetStatValue(StatDefOf.NegotiationAbility, true)).First();
                    bool ok = InvokeLetter(l, "Accept");
                    if (!ok) { try { q.Accept(who); ok = true; } catch { } }
                    if (ok) Mind.Think("квест", "Принял квест «" + q.name + "» через письмо: безопасный тип " + root + ", колонистов " + cols.Count + ".");
                }
            }
            catch (Exception e) { T.Warn("Квестовые письма: " + e.Message); }
        }

        private static bool InvokeLetter(object letter, string method)
        {
            if (letter == null) return false;
            try
            {
                MethodInfo m = letter.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (m == null) return false;
                m.Invoke(letter, null);
                return true;
            }
            catch { return false; }
        }

        public static void Run(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            if (cols.Count == 0) return;
            AcceptLetters(cols);
            foreach (Quest q in Find.QuestManager.questsInDisplayOrder.ToList())
            {
                if (q.State != QuestState.NotYetAccepted || q.hidden || q.Historical) continue;
                if (!decided.Add(q.id)) continue;
                string root = q.root != null ? q.root.defName : "?";
                string name = q.name;
                bool safe = Safe.Any(w => root.Contains(w));
                bool strong = cols.Count >= 3;
                if (safe && strong)
                {
                    Pawn who = cols.OrderByDescending(p => p.GetStatValue(StatDefOf.NegotiationAbility, true)).First();
                    q.Accept(who);
                    Mind.Think("квест", "Принял квест «" + name + "» (" + root + "): выполним на карте, колонистов " + cols.Count + ".");
                }
                else
                    Mind.Think("квест", "Пропускаю «" + name + "» (" + root + "): " + (safe ? "колония слишком слаба (<3 человек)" : "нужен караван или риск неизвестен") + ".");
            }
        }
    }
}

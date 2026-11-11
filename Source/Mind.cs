using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Мысли бота: лента решений и понятная сводка «что сейчас важнее всего и почему».</summary>
    public static class Mind
    {
        public class Thought { public int tick; public string cat, text; }
        public class Concern { public string name; public float severity; public string why; public string doing; }

        public static readonly List<Thought> Log = new List<Thought>();
        public static List<Concern> Concerns = new List<Concern>();
        public static string Headline = "";
        public static string Why = "";
        private static float pausedSince = -1f;
        private static int lastAssessTick = -1;

        public static void Think(string cat, string text)
        {
            int tick = 0;
            try { if (Find.TickManager != null) tick = Find.TickManager.TicksGame; } catch { }
            if (Log.Count > 0 && Log[Log.Count - 1].text == text) return;
            Log.Add(new Thought { tick = tick, cat = cat, text = text });
            if (Log.Count > 500) Log.RemoveRange(0, 100);
        }

        /// <summary>Сводка по миру: без тяжёлых повторных расчётов — берём уже собранную картину.</summary>
        public static void Assess(ColonyWorld w)
        {
            if (w == null) return;
            if (w.tick == lastAssessTick) return;
            lastAssessTick = w.tick;

            List<Concern> list = new List<Concern>();
            list.Add(new Concern { name = "Еда", severity = U.Clamp01(1f - w.foodDays / Mathf.Max(4f, w.foodTargetDays)),
                why = "запас " + U.F(w.foodDays) + " дн. (с урожаем " + U.F(w.foodDaysWithCrops) + "), тренд " + U.Sign(w.foodTrend) + "/дн.",
                doing = w.foodDays < 3f ? "дикая еда, охота, запрет продажи" : "грядки, готовка, запас" });
            list.Add(new Concern { name = "Угроза", severity = w.threat
                    ? (w.hostilesNear > 0 ? U.Clamp01(0.5f + w.threatRatio * 0.5f) : U.Clamp01(0.22f + w.threatRatio * 0.25f))
                    : U.Clamp01(w.baseRisk * 0.6f),
                why = w.threat ? (w.hostiles + " врагов, сила " + U.Pct(w.threatRatio) + " от нашей") : ("риск рейда " + U.Pct(w.baseRisk)),
                doing = w.threat ? "защитники, укрытия, отступление слабых" : "турели, ловушки, подготовка" });
            list.Add(new Concern { name = "Здоровье", severity = U.Clamp01(w.healthUrgency),
                why = "лежачих " + w.downed + ", кровотечений " + w.bleeding + ", нужен врач " + w.needsTend,
                doing = w.downed + w.bleeding > 0 ? "врач, лекарства, отдых" : "в норме" });
            list.Add(new Concern { name = "Настроение", severity = U.Clamp01(w.moodUrgency),
                why = "среднее " + U.Pct(w.moodAvg) + ", срывов " + w.mental, doing = w.moodAvg < 0.45f ? "отдых, радость, вечеринка" : "в норме" });
            list.Add(new Concern { name = "Отдых", severity = U.Clamp01((0.3f - w.restMin) * 3f),
                why = "минимум " + U.Pct(w.restMin) + ", вымотаны " + w.exhausted, doing = w.restMin < 0.3f ? "сон по расписанию" : "в норме" });
            list.Add(new Concern { name = "Температура", severity = U.Clamp01(Mathf.Max(0f, Mathf.Abs(w.outdoorTemp - 21f) - 18f) / 20f + w.hypothermia * 0.3f),
                why = w.outdoorTemp.ToString("0") + "°C" + (w.hypothermia > 0 ? ", переохлаждение " + w.hypothermia : ""),
                doing = w.outdoorTemp < -2f ? "костёр, тёплая одежда" : w.outdoorTemp > 36f ? "работа в тени" : "комфортно" });
            list.Add(new Concern { name = "Энергия", severity = U.Clamp01(w.powerDeficit / Mathf.Max(200f, w.powerDemand * 0.5f)),
                why = (int)w.powerCapacity + "/" + (int)w.powerDemand + " Вт, дефицит " + (int)w.powerDeficit,
                doing = w.powerDeficit > 0 ? "один генератор, провода" : "в норме" });
            try
            {
                FoodSafety.Report fs = FoodSafety.Analyze(w.map);
                if (fs.totalNutrition > 0f && fs.spoilPct > 0.05f)
                    list.Add(new Concern { name = "Порча еды", severity = U.Clamp01(fs.spoilPct * 1.2f),
                        why = "сгниёт " + U.Pct(fs.spoilPct) + " запаса, до порчи успеем съесть " + U.F(fs.safeDays) + " дн.",
                        doing = "крытый холодный склад, доступ к еде и долгий запас" });
                Deterioration.Report ds = Deterioration.Analyze(w.map);
                if (ds.count > 0)
                    list.Add(new Concern { name = "Ветшание вещей", severity = U.Clamp01(ds.count / 8f + (ds.worstDays < 5f ? 0.25f : 0f)),
                        why = ds.count + " вещей на улице, худшая " + U.Pct(ds.worstHpPct) + " прочности",
                        doing = "убрать под крышу в склад с приоритетом Critical" });
            }
            catch { }
            list.Add(new Concern { name = "Развитие", severity = U.Clamp01(0.55f - w.opportunity * 0.4f),
                why = "богатство " + (int)w.wealth + " (" + U.Sign(w.wealthTrend) + "/дн.), верстаков " + w.tables + ", заданий " + w.bills,
                doing = "прибыльные задания, торговля, наука" });
            foreach (string cond in w.conditions)
                list.Add(new Concern { name = "Событие", severity = 0.55f, why = cond, doing = cond.Contains("Токсич") || cond.Contains("Toxic") ? "все в укрытие" : "наблюдаю" });

            Concerns = list.OrderByDescending(c => c.severity).ToList();
            Concern top = Concerns.FirstOrDefault();
            Headline = top != null && top.severity > 0.18f
                ? "Главная забота: " + top.name + " — " + top.why + " → " + top.doing
                : "Всё стабильно: развиваю экономику, науку и запас на будущее.";
            Why = w.Brief();
            BrainComponent brain = BrainComponent.Get();
            if (brain != null && brain.Current != null && brain.Current.utility > 0.25f)
                Headline = "Цель «" + brain.Current.name + "»: " + brain.Current.why;
        }

        /// <summary>Совместимость со старым кодом, который передавал карту.</summary>
        public static void Assess(Map map)
        {
            if (map == null) return;
            Assess(ColonyWorld.Build(map, null));
        }

        // ---------- GUI ----------
        public static void OnGUI()
        {
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || Current.ProgramState != ProgramState.Playing) return;

            // Автоснятие паузы (GameComponentTick при паузе не идёт, поэтому здесь)
            if (s.enabled && s.keepRunning && Find.TickManager.Paused && !Find.WindowStack.WindowsForcePause)
            {
                if (pausedSince < 0) pausedSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - pausedSince > 2f)
                {
                    Find.TickManager.CurTimeSpeed = s.fastSpeed || s.calmSpeed >= 3 ? TimeSpeed.Superfast : TimeSpeed.Fast;
                    pausedSince = -1f;
                    Think("пауза", "Игра стояла на паузе — продолжаю на высокой скорости (можно отключить в настройках).");
                }
            }
            else pausedSince = -1f;

            T.Safe("Control", Control.OnGUI);
            T.Safe("AutoClose", AutoClose.OnGUI);

            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.T) { Toggle(); e.Use(); }

            if (s.showHud && Find.CurrentMap != null && Find.WindowStack.WindowOfType<BrainWindow>() == null)
            {
                if (Widgets.ButtonText(new Rect(UI.screenWidth - 150f, UI.screenHeight - 180f, 140f, 28f), "Мозг бота"))
                    Toggle();
            }
        }

        public static void Toggle()
        {
            BrainWindow w = Find.WindowStack.WindowOfType<BrainWindow>();
            if (w != null) w.Close();
            else Find.WindowStack.Add(new BrainWindow());
        }

        public static void ResetRuntime()
        {
            AutoClose.ResetRuntime();
            Log.Clear();
            Concerns.Clear();
            Headline = "";
            Why = "";
            lastAssessTick = -1;
            pausedSince = -1f;
        }

        public static string LearningLine()
        {
            BrainComponent b = BrainComponent.Get();
            if (b == null) return Learning.Summary();
            return b.LearningLine();
        }
    }
}

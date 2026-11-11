using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Мысли бота: лента решений + оценка «что сейчас важнее всего» с объяснениями.</summary>
    public static class Mind
    {
        public class Thought { public int tick; public string cat, text; }
        public class Concern { public string name; public float severity; public string why; public string doing; }

        public static readonly List<Thought> Log = new List<Thought>();
        public static List<Concern> Concerns = new List<Concern>();
        public static string Headline = "";
        private static float pausedSince = -1f;

        public static void Think(string cat, string text)
        {
            int tick = 0;
            try { if (Find.TickManager != null) tick = Find.TickManager.TicksGame; } catch { }
            if (Log.Count > 0 && Log[Log.Count - 1].text == text) return;
            Log.Add(new Thought { tick = tick, cat = cat, text = text });
            if (Log.Count > 400) Log.RemoveRange(0, 100);
        }

        public static void Assess(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            var list = new List<Concern>();
            if (cols.Count == 0) { Concerns = list; return; }

            // Еда
            float nut = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (d.IsNutritionGivingIngestible && !d.IsDrug && d.category == ThingCategory.Item)
                    nut += T.Count(map, d) * d.GetStatValueAbstract(StatDefOf.Nutrition);
            float days = nut / Math.Max(1f, cols.Count * 2.6f);
            list.Add(new Concern { name = "Еда", severity = Mathf.Clamp01(1f - days / 8f), why = "запас на " + days.ToString("0.0") + " дн.",
                doing = days < 3 ? "дикие растения, охота, срочные грядки" : "поддерживаю запас" });

            // Угроза
            bool threat = GenHostility.AnyHostileActiveThreatToPlayer(map);
            list.Add(new Concern { name = "Угроза", severity = threat ? 1f : 0.05f, why = threat ? "враги на карте" : "спокойно",
                doing = threat ? "призыв защитников, отступление раненых, турели/укрытия" : "укрепления, ловушки" });

            // Здоровье
            int downed = cols.Count(p => p.Downed);
            int bleeding = cols.Count(p => p.health.hediffSet.BleedRateTotal > 0.1f);
            list.Add(new Concern { name = "Здоровье", severity = Mathf.Clamp01((downed * 0.4f + bleeding * 0.3f) / Math.Max(1, cols.Count) * 2f),
                why = "упавших " + downed + ", кровотечений " + bleeding, doing = downed + bleeding > 0 ? "врач + лечение" : "в норме" });

            // Настроение / отдых / гигиена
            float mood = Avg(cols, p => p.needs != null && p.needs.mood != null ? p.needs.mood.CurLevelPercentage : 0.6f);
            list.Add(new Concern { name = "Настроение", severity = Mathf.Clamp01((0.6f - mood) * 3f), why = "среднее " + (int)(mood * 100) + "%",
                doing = mood < 0.4f ? "отдых/радость, вечеринка" : "в норме" });
            float rest = Min(cols, p => p.needs != null && p.needs.rest != null ? p.needs.rest.CurLevelPercentage : 1f);
            list.Add(new Concern { name = "Усталость", severity = Mathf.Clamp01((0.25f - rest) * 4f), why = "минимум " + (int)(rest * 100) + "%", doing = rest < 0.25f ? "принудительный сон" : "в норме" });
            NeedDef hyg = DefDatabase<NeedDef>.GetNamedSilentFail("Hygiene");
            if (hyg != null)
            {
                float h = Min(cols, p => { var n = p.needs?.TryGetNeed(hyg); return n != null ? n.CurLevelPercentage : 1f; });
                list.Add(new Concern { name = "Гигиена", severity = Mathf.Clamp01((0.3f - h) * 3f), why = "минимум " + (int)(h * 100) + "%", doing = h < 0.3f ? "строю умывальники/туалеты" : "в норме" });
            }

            // Погода
            float temp = map.mapTemperature.OutdoorTemp;
            list.Add(new Concern { name = "Погода", severity = Mathf.Clamp01(Math.Max(-temp - 5f, temp - 38f) / 20f), why = temp.ToString("0") + "°C",
                doing = temp < -5 ? "костёр, тёплая одежда" : temp > 38 ? "работа в помещениях" : "комфортно" });

            // События
            foreach (GameCondition gc in map.gameConditionManager.ActiveConditions)
                list.Add(new Concern { name = "Событие", severity = 0.6f, why = gc.def.LabelCap, doing = gc.def == GameConditionDefOf.ToxicFallout ? "все в укрытие" : "наблюдаю" });

            Concerns = list.OrderByDescending(c => c.severity).ToList();
            var top = Concerns.FirstOrDefault();
            Headline = top != null && top.severity > 0.15f ? "Главная забота: " + top.name + " — " + top.why + " → " + top.doing : "Всё стабильно: развиваюсь (торговля, постройки, исследования).";
        }

        private static float Avg(List<Pawn> l, Func<Pawn, float> f) { return l.Count == 0 ? 0f : l.Average(f); }
        private static float Min(List<Pawn> l, Func<Pawn, float> f) { return l.Count == 0 ? 1f : l.Min(f); }

        // ---------- GUI ----------
        public static void OnGUI()
        {
            var s = TradeEmpireMod.S;
            if (s == null || Current.ProgramState != ProgramState.Playing) return;

            // Автоснятие паузы (GameComponentTick при паузе не идёт, поэтому здесь)
            if (s.enabled && s.keepRunning && Find.TickManager.Paused && !Find.WindowStack.WindowsForcePause)
            {
                if (pausedSince < 0) pausedSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - pausedSince > 3f)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Fast; pausedSince = -1f;
                    Think("пауза", "Игра стояла на паузе — продолжаю (можно отключить в настройках/панели).");
                }
            }
            else pausedSince = -1f;

            T.Safe("Control", Control.OnGUI);

            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.T) { Toggle(); e.Use(); }

            if (s.showHud && Find.CurrentMap != null && !WorldRendererUtility.WorldRenderedNow && Find.WindowStack.WindowOfType<ThoughtsWindow>() == null)
                if (Widgets.ButtonText(new Rect(UI.screenWidth - 150f, UI.screenHeight - 180f, 140f, 28f), "Мысли бота"))
                    Toggle();
        }

        public static void Toggle()
        {
            var w = Find.WindowStack.WindowOfType<ThoughtsWindow>();
            if (w != null) w.Close(); else Find.WindowStack.Add(new ThoughtsWindow());
        }
    }

    public class ThoughtsWindow : Window
    {
        private Vector2 scroll;
        private string filter = "";
        private static readonly string[] Cats = { "", "решение", "действие", "событие", "гигиена", "идеология", "пауза", "квест", "время", "энергия", "работа", "зависание" };

        public ThoughtsWindow()
        {
            draggable = true; resizeable = true; doCloseX = true; preventCameraMotion = false;
            absorbInputAroundWindow = false; closeOnClickedOutside = false; forcePause = false;
        }

        public override Vector2 InitialSize { get { return new Vector2(560f, 640f); } }

        public override void DoWindowContents(Rect inRect)
        {
            var s = TradeEmpireMod.S;
            float y = 0f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 30f), "Мысли бота"); y += 30f;
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0, y, inRect.width, 44f), Mind.Headline); y += 46f;

            // Заботы
            foreach (Mind.Concern c in Mind.Concerns.Take(8))
            {
                var bar = new Rect(0, y, 90f, 16f);
                Widgets.DrawBox(bar);
                GUI.color = Color.Lerp(Color.green, Color.red, c.severity);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * c.severity, bar.height), BaseContent.WhiteTex);
                GUI.color = Color.white;
                Widgets.Label(new Rect(96f, y - 2f, inRect.width - 96f, 22f), c.name + ": " + c.why + " → " + c.doing);
                y += 20f;
            }
            y += 4f;
            Widgets.Label(new Rect(0, y, inRect.width, 22f), Learning()); y += 22f;
            Widgets.Label(new Rect(0, y, inRect.width, 22f), Ledger.Summary()); y += 26f;

            // Переключатели
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width / 2f, 24f), "Авто-запуск при паузе", ref s.keepRunning);
            Widgets.CheckboxLabeled(new Rect(inRect.width / 2f, y, inRect.width / 2f, 24f), "Самообучение", ref s.learning); y += 26f;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width / 2f, 24f), "Торговая империя", ref s.orbitalTrade);
            Widgets.CheckboxLabeled(new Rect(inRect.width / 2f, y, inRect.width / 2f, 24f), "Мозг (кризисы/события)", ref s.brain); y += 28f;

            if (Widgets.ButtonText(new Rect(0, y, 150f, 24f), "Фильтр: " + (filter == "" ? "все" : filter)))
            {
                int i = Array.IndexOf(Cats, filter); filter = Cats[(i + 1) % Cats.Length];
            }
            y += 28f;

            var outRect = new Rect(0, y, inRect.width, inRect.height - y);
            var items = Mind.Log.Where(t => filter == "" || t.cat == filter).Reverse().Take(150).ToList();
            var view = new Rect(0, 0, outRect.width - 18f, items.Count * 44f);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float yy = 0f;
            foreach (Mind.Thought t in items)
            {
                string head = "[д" + (t.tick / 60000) + " " + ((t.tick % 60000) / 2500) + "ч] " + t.cat + ": ";
                float h = Text.CalcHeight(head + t.text, view.width);
                Widgets.Label(new Rect(0, yy, view.width, h), head + t.text);
                yy += h + 2f;
            }
            Widgets.EndScrollView();
        }

        private static string Learning()
        {
            try
            {
                var s = TradeEmpireMod.S;
                if (!s.learning) return "Обучение выключено.";
                int n = Memory.Arms.Values.Sum(a => a.n);
                return "Обучение: накоплено опытов " + n + " (вариантов " + Memory.Arms.Count + "), файл Config/RimBotBrain.txt";
            }
            catch { return "Обучение: —"; }
        }
    }
}

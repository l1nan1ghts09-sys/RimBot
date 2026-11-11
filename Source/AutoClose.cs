using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Наводит порядок в интерфейсе: закрывает всплывающие окна, убирает письма-уведомления
    /// и всплывающие сообщения. Важные выборы сначала обрабатывает Control/Quests,
    /// поэтому окна закрываются с небольшой задержкой, а не мгновенно.
    /// </summary>
    public static class AutoClose
    {
        private static float nextRun;
        private static readonly Dictionary<Window, float> seen = new Dictionary<Window, float>();
        private static readonly HashSet<string> recentlyLogged = new HashSet<string>();

        public static int ClosedWindows { get; private set; }
        public static int ClosedLetters { get; private set; }
        public static int ClosedMessages { get; private set; }
        public static string LastAction = "";

        // Окна, которые нельзя закрывать: настройки, меню и собственная панель бота.
        private static readonly string[] KeepTypes =
        {
            "ModSettings", "ModsConfig", "Dialog_Options", "Page_Options",
            "Options", "Settings", "BrainWindow", "Dialog_Dev", "EditWindow",
            "MainMenu", "Menu", "LoadingScreen"
        };

        // Что считаем оповещением, а не рабочим окном игрока. Закрываем только
        // такие окна и только если они останавливают игру (forcePause).
        private static readonly string[] NotificationTypes =
        {
            "Dialog_MessageBox", "Dialog_NodeTree", "Dialog_Confirm",
            "Dialog_Information", "Dialog_Message", "Dialog_Text",
            "Dialog_Choice", "Dialog_Feedback"
        };

        public static void ResetRuntime()
        {
            nextRun = 0f;
            seen.Clear();
            recentlyLogged.Clear();
            ClosedWindows = ClosedLetters = ClosedMessages = 0;
            LastAction = "";
        }

        public static string Line()
        {
            if (ClosedWindows + ClosedLetters + ClosedMessages == 0)
                return "Оповещения: пока нечего закрывать.";
            return "Оповещения: закрыто окон-оповещений " + ClosedWindows
                + ", писем " + ClosedLetters + ", сообщений " + ClosedMessages + ".";
        }

        public static void OnGUI()
        {
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.enabled) return;
            if (Current.ProgramState != ProgramState.Playing) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            float now = Time.realtimeSinceStartup;
            if (now < nextRun) return;
            nextRun = now + 0.5f;

            if (s.autoCloseWindows) T.Safe("AutoClose.Windows", () => CloseWindows(now));
            if (s.autoCloseNotifications)
            {
                T.Safe("AutoClose.Letters", CloseLetters);
                T.Safe("AutoClose.Messages", CloseMessages);
            }
        }

        // ------------------------------------------------------------- окна

        private static void CloseWindows(float now)
        {
            WindowStack stack = Find.WindowStack;
            if (stack == null) return;

            List<Window> windows = stack.Windows.ToList();
            HashSet<Window> alive = new HashSet<Window>(windows);
            foreach (Window w in seen.Keys.Where(k => !alive.Contains(k)).ToList()) seen.Remove(w);

            Window closing = null;
            int closedNow = 0;
            // Сверху вниз: сначала убираем верхнюю модалку, чтобы не закрыть её родителя.
            foreach (Window w in windows.AsEnumerable().Reverse())
            {
                if (w == null) continue;
                string n = w.GetType().Name;
                if (!ShouldClose(w, n)) continue;

                float first;
                if (!seen.TryGetValue(w, out first))
                {
                    seen[w] = now;
                    continue;
                }

                float age = now - first;
                float grace = GraceFor(w, n);
                if (age < grace) continue;

                try
                {
                    w.Close(true);
                    if (w is Dialog_Trade)
                    {
                        try { if (TradeSession.Active) TradeSession.Close(); } catch { }
                    }
                    ClosedWindows++;
                    closing = w;
                    LastAction = "Закрыл окно «" + FriendlyName(n) + "».";
                }
                catch (Exception e)
                {
                    T.Warn("Не удалось закрыть окно " + n + ": " + e.Message);
                }
                if (++closedNow >= 4) break; // не растягиваем уборку на десятки секунд
            }

            if (closing != null)
            {
                string key = closing.GetType().Name;
                if (recentlyLogged.Add(key))
                    Mind.Think("окна", LastAction);
            }
        }

        private static bool ShouldClose(Window w, string n)
        {
            if (w == null) return false;
            if (w is BrainWindow || w is FloatMenu) return false;
            if (KeepType(n)) return false;

            // Меню и настройки не трогаем ни при каких обстоятельствах.
            if (!w.forcePause) return false;              // только окна, которые останавливают игру

            // Не закрываем торговлю, счета, настройки зданий и прочие рабочие окна.
            foreach (string type in NotificationTypes)
                if (!string.IsNullOrEmpty(n) && n.IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static bool KeepType(string n)
        {
            foreach (string keep in KeepTypes)
                if (!string.IsNullOrEmpty(n) && n.IndexOf(keep, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            if (!string.IsNullOrEmpty(n) && n.IndexOf("RimWorldBot", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private static float GraceFor(Window w, string n)
        {
            // Событийному диалогу даём шанс: Control выбирает вариант примерно через 3 секунды.
            if (w is Dialog_NodeTree) return 4.5f;
            // Торговое окно ведёт AutoTrader; не выдёргиваем его из-под незавершённой сделки.
            if (w is Dialog_Trade) return TradeSession.Active ? 12f : 2.5f;
            return 1.2f;
        }

        private static string FriendlyName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "?";
            if (typeName.IndexOf("MessageBox", StringComparison.OrdinalIgnoreCase) >= 0) return "подтверждение";
            if (typeName.IndexOf("NodeTree", StringComparison.OrdinalIgnoreCase) >= 0) return "событие";
            if (typeName.IndexOf("Trade", StringComparison.OrdinalIgnoreCase) >= 0) return "торговля";
            if (typeName.IndexOf("MainTab", StringComparison.OrdinalIgnoreCase) >= 0) return "вкладка";
            if (typeName.IndexOf("FloatMenu", StringComparison.OrdinalIgnoreCase) >= 0) return "меню";
            return typeName;
        }

        // -------------------------------------------------------- уведомления

        private static void CloseLetters()
        {
            LetterStack letters = Find.LetterStack;
            if (letters == null) return;

            int removed = 0;
            foreach (Letter l in letters.LettersListForReading.ToList())
            {
                if (l == null) continue;
                try
                {
                    // В 1.6 это ровно те письма, которые игрок может убрать правым кликом.
                    // Письма с обязательным выбором остаются, пока с ними не разберутся Control/Quests.
                    if (!l.CanDismissWithRightClick) continue;
                    letters.RemoveLetter(l);
                    removed++;
                }
                catch (Exception e)
                {
                    T.Warn("Не удалось убрать письмо: " + e.Message);
                }
            }

            if (removed > 0)
            {
                ClosedLetters += removed;
                LastAction = "Убрал писем-уведомлений: " + removed + ".";
                if (recentlyLogged.Add("letters"))
                    Mind.Think("уведомления", LastAction);
            }
        }

        private static void CloseMessages()
        {
            try
            {
                Type messages = typeof(Messages);
                int before = LiveMessageCount(messages);
                if (before <= 0) return;

                MethodInfo clear = messages.GetMethod("Clear",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (clear != null)
                {
                    clear.Invoke(null, null);
                }
                else
                {
                    // Запасной путь для версий, где Clear нет: чистим liveMessages напрямую.
                    IList list = LiveMessages(messages);
                    list?.Clear();
                }

                int after = LiveMessageCount(messages);
                int removed = Math.Max(0, before - after);
                if (removed > 0)
                {
                    ClosedMessages += removed;
                    LastAction = "Убрал всплывающих сообщений: " + removed + ".";
                    if (recentlyLogged.Add("messages"))
                        Mind.Think("уведомления", LastAction);
                }
            }
            catch (Exception e)
            {
                T.Warn("Не удалось очистить сообщения: " + e.Message);
            }
        }

        private static int LiveMessageCount(Type messages)
        {
            try
            {
                IList list = LiveMessages(messages);
                return list != null ? list.Count : 0;
            }
            catch { return 0; }
        }

        private static IList LiveMessages(Type messages)
        {
            FieldInfo live = messages.GetField("liveMessages",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (live != null)
            {
                IList direct = live.GetValue(null) as IList;
                if (direct != null) return direct;
            }
            // В некоторых сборках поле называется иначе: ищем статический список Message.
            foreach (FieldInfo f in messages.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
            {
                if (!f.FieldType.IsGenericType) continue;
                Type[] args = f.FieldType.GetGenericArguments();
                if (args.Length != 1 || args[0] != typeof(Message)) continue;
                IList list = f.GetValue(null) as IList;
                if (list != null) return list;
            }
            return null;
        }
    }
}

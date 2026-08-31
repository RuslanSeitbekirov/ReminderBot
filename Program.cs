using Microsoft.EntityFrameworkCore;
using ReminderBot.Data;
using ReminderBot.Models;
using ReminderBot.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ReminderBot
{
    class Program
    {
        private static readonly Dictionary<long, (int State, Reminder? TempReminder)> _userStates = new();
        private static ReminderService _reminderService = null!;
        private static ITelegramBotClient _botClient = null!;
        private static AppDbContext _context = null!;

        static async Task Main(string[] args)
        {
            LoadEnvFile();

            _context = new AppDbContext();  // ← Используем только статическое поле
            await _context.Database.EnsureCreatedAsync();
            _reminderService = new ReminderService(_context);

                var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    
            if (string.IsNullOrEmpty(botToken))
            {
                Console.WriteLine("❌ Ошибка: BOT_TOKEN не найден!");
                Console.WriteLine("Создайте файл .env с переменной BOT_TOKEN=ваш_токен");
                return;
            }
            _botClient = new TelegramBotClient(botToken);

            using var cts = new CancellationTokenSource();
            
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
            };

            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                receiverOptions,
                cts.Token
            );

            _ = CheckRemindersLoop(cts.Token);

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await _context.SaveChangesAsync();
                        Console.WriteLine("[DB] Periodic save completed.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DB Save Error] {ex.Message}");
                    }
                    await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
                }
            }, cts.Token);

            Console.WriteLine("Бот запущен. Нажмите Enter для остановки...");
            Console.ReadLine();
            cts.Cancel();
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
        {
            if (update.CallbackQuery != null)
            {
                await HandleCallbackQueryAsync(botClient, update.CallbackQuery);
                return;
            }

            if (update.Message == null) return;

            var userId = update.Message.From.Id;
            var username = update.Message.From.Username;
            var text = update.Message.Text;

            var user = await _reminderService.GetOrCreateUserAsync(userId, username);

            if (text?.StartsWith('/') == true)
            {
                await HandleCommandAsync(botClient, update.Message, user, text);
            }
            else
            {
                await HandleInputAsync(botClient, update.Message, user, text);
            }
        }

        private static async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callback)
        {
            var userId = callback.From.Id;
            var data = callback.Data;
            if (data == null)
            {
                await botClient.AnswerCallbackQueryAsync(callback.Id);
                return;
            }

            Console.WriteLine($"[Callback] User {userId}, Data: {data}");

            try
            {
                // ===== СОЗДАНИЕ: выбор расписания =====
                if (data.StartsWith("schedule_"))
                {
                    var scheduleType = data.Replace("schedule_", "");
                    scheduleType = char.ToUpper(scheduleType[0]) + scheduleType.Substring(1);

                    var stateData = _userStates.GetValueOrDefault(userId);
                    var tempReminder = stateData.TempReminder;

                    if (tempReminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId,
                            "⚠️ Ошибка: сессия прервана. Начните заново с /create_note");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    tempReminder.ScheduleType = scheduleType;

                    if (scheduleType == "OneTime")
                    {
                        // Для разового — сразу запрашиваем время
                        _userStates[userId] = (UserState.EnteringTime, tempReminder);
                        await botClient.SendTextMessageAsync(userId,
                            "🕐 Введите время в формате ЧЧ:ММ (например, 14:30):");
                    }
                    else
                    {
                        switch (scheduleType)
                        {
                            case "Daily":
                                _userStates[userId] = (UserState.EnteringTime, tempReminder);
                                await botClient.SendTextMessageAsync(userId,
                                    "🕐 Введите время в формате ЧЧ:ММ:");
                                break;
                            case "Interval":
                                _userStates[userId] = (UserState.EnteringInterval, tempReminder);
                                await botClient.SendTextMessageAsync(userId,
                                    "⏰ Введите интервал в минутах или Ч:ММ:");
                                break;
                            case "Weekly":
                                _userStates[userId] = (UserState.EnteringWeekDays, tempReminder);
                                await botClient.SendTextMessageAsync(userId,
                                    "📆 Введите дни недели через запятую (1=Пн, ..., 7=Вс):");
                                break;
                        }
                    }
                }
                // ===== РЕДАКТИРОВАНИЕ: выбор напоминания =====
                else if (data.StartsWith("edit_") && !data.Contains("_title") && !data.Contains("_text")
                        && !data.Contains("_schedule") && !data.Contains("_prenotif") && !data.Contains("_status"))
                {
                    var reminderId = int.Parse(data.Replace("edit_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    _userStates[userId] = (UserState.EditingReminderMenu, reminder);
                    await SendEditMenuAsync(botClient, userId, reminder);
                }
                // ===== РЕДАКТИРОВАНИЕ: изменить название =====
                else if (data.StartsWith("edit_title_"))
                {
                    var reminderId = int.Parse(data.Replace("edit_title_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }
                    _userStates[userId] = (UserState.EditingTitle, reminder);
                    await botClient.SendTextMessageAsync(userId,
                        $"✏️ Текущее название: \"{reminder.Title}\"\nВведите новое название:");
                }
                // ===== РЕДАКТИРОВАНИЕ: изменить текст =====
                else if (data.StartsWith("edit_text_"))
                {
                    var reminderId = int.Parse(data.Replace("edit_text_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }
                    _userStates[userId] = (UserState.EditingText, reminder);
                    await botClient.SendTextMessageAsync(userId,
                        $"✏️ Текущий текст: \"{reminder.Text}\"\nВведите новый текст:");
                }
                // ===== РЕДАКТИРОВАНИЕ: изменить расписание =====
                else if (data.StartsWith("edit_schedule_"))
                {
                    var reminderId = int.Parse(data.Replace("edit_schedule_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    var scheduleKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("📅 Каждый день", $"editsched_daily_{reminderId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("⏰ Интервал", $"editsched_interval_{reminderId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("📆 По дням недели", $"editsched_weekly_{reminderId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("🔹 Один раз", $"editsched_onetime_{reminderId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"edit_{reminderId}") }
                    });
                    await botClient.SendTextMessageAsync(userId,
                        "Выберите новый тип расписания:", replyMarkup: scheduleKeyboard);
                }
                // ===== РЕДАКТИРОВАНИЕ: выбор нового типа расписания =====
                else if (data.StartsWith("editsched_"))
                {
                    var parts = data.Replace("editsched_", "").Split('_');
                    var scheduleType = char.ToUpper(parts[0][0]) + parts[0].Substring(1);
                    var reminderId = int.Parse(parts[1]);

                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    reminder.ScheduleType = scheduleType;
                    _userStates[userId] = (UserState.EditingSchedule, reminder);

                    switch (scheduleType)
                    {
                        case "Daily":
                        case "OneTime":
                            await botClient.SendTextMessageAsync(userId,
                                "🕐 Введите новое время в формате ЧЧ:ММ:");
                            break;
                        case "Interval":
                            await botClient.SendTextMessageAsync(userId,
                                "⏰ Введите новый интервал в минутах или Ч:ММ:");
                            break;
                        case "Weekly":
                            await botClient.SendTextMessageAsync(userId,
                                "📆 Введите новые дни недели через запятую (1=Пн, ..., 7=Вс):");
                            break;
                    }
                }
                // ===== РЕДАКТИРОВАНИЕ: предварительные уведомления =====
                else if (data.StartsWith("edit_prenotif_"))
                {
                    var reminderId = int.Parse(data.Replace("edit_prenotif_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }
                    _userStates[userId] = (UserState.EditingPreNotifications, reminder);

                    var current = string.IsNullOrEmpty(reminder.PreNotificationOffsets)
                        ? "не заданы"
                        : reminder.PreNotificationOffsets;

                    await botClient.SendTextMessageAsync(userId,
                        $"🔔 Текущие пред-уведомления (минуты до события): {current}\n\n" +
                        "Введите новые значения через запятую (например: 1440,60,15 для 1 дня, 1 часа, 15 мин)\n" +
                        "Или введите 0 чтобы отключить:");
                }
                // ===== РЕДАКТИРОВАНИЕ: вкл/выкл =====
                else if (data.StartsWith("toggle_status_"))
                {
                    var reminderId = int.Parse(data.Replace("toggle_status_", ""));
                    var success = await _reminderService.ToggleReminderStatusAsync(userId, reminderId);

                    if (success)
                    {
                        var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                        if (reminder != null)
                        {
                            _userStates[userId] = (UserState.EditingReminderMenu, reminder);
                            await botClient.SendTextMessageAsync(userId,
                                $"✅ Статус изменён: {(reminder.IsActive ? "🟢 Активно" : "⚫ Неактивно")}");
                            await SendEditMenuAsync(botClient, userId, reminder);
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(userId, "❌ Ошибка изменения статуса.");
                    }
                }
                // ===== НАЗАД в меню редактирования =====
                else if (data == "edit_back")
                {
                    _userStates.Remove(userId);
                    await botClient.SendTextMessageAsync(userId, "◀️ Возврат в главное меню.");
                }
                // ===== УДАЛЕНИЕ =====
                else if (data.StartsWith("delete_"))
                {
                    var reminderId = int.Parse(data.Replace("delete_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }
                    _userStates[userId] = (UserState.DeletingReminder, reminder);
                    var confirmKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("✅ Да", $"confirm_delete_{reminderId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("❌ Нет", "cancel_delete") }
                    });
                    await botClient.SendTextMessageAsync(userId,
                        $"Вы уверены, что хотите удалить \"{reminder.Title}\"?",
                        replyMarkup: confirmKeyboard);
                }
                else if (data.StartsWith("confirm_delete_"))
                {
                    var reminderId = int.Parse(data.Replace("confirm_delete_", ""));
                    var success = await _reminderService.DeleteReminderAsync(userId, reminderId);
                    _userStates.Remove(userId);
                    if (success)
                        await botClient.SendTextMessageAsync(userId, "✅ Напоминание удалено. Нумерация обновлена.");
                    else
                        await botClient.SendTextMessageAsync(userId, "❌ Ошибка при удалении.");
                }
                else if (data == "cancel_delete")
                {
                    _userStates.Remove(userId);
                    await botClient.SendTextMessageAsync(userId, "❌ Удаление отменено.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Callback Error] {ex.Message}");
                await botClient.SendTextMessageAsync(userId,
                    $"⚠️ Ошибка: {ex.Message}\nПопробуйте начать заново.");
            }

            await botClient.AnswerCallbackQueryAsync(callback.Id);
        }

        // Вспомогательный метод: показать меню редактирования напоминания
        private static async Task SendEditMenuAsync(ITelegramBotClient botClient, long userId, Reminder reminder)
        {
            var statusIcon = reminder.IsActive ? "🟢" : "⚫";
            var statusText = reminder.IsActive ? "Активно" : "Неактивно";

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить название", $"edit_title_{reminder.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить текст", $"edit_text_{reminder.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("📅 Изменить расписание", $"edit_schedule_{reminder.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("🔔 Пред-уведомления", $"edit_prenotif_{reminder.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData(
                    reminder.IsActive ? "⚫ Выключить" : "🟢 Включить",
                    $"toggle_status_{reminder.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад к списку", "edit_back") }
            });

            await botClient.SendTextMessageAsync(userId,
                $"📝 <b>Редактирование:</b> \"{EscapeHtml(reminder.Title)}\"\n" +
                $"Статус: {statusIcon} {statusText}\n" +
                $"Расписание: {GetScheduleDescription(reminder)}",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard);
        }
        private static async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, AppUser user, string command)
        {
            var userId = user.TelegramId;
            switch (command)
            {
                case "/create_note":
                    _userStates[userId] = (UserState.EnteringTitle, null);
                    await botClient.SendTextMessageAsync(userId,
                        "📝 Введите название напоминания (только текст и цифры):");
                    break;

                case "/edit_note":
                    var reminders = await _reminderService.GetAllRemindersAsync(userId);
                    if (reminders.Count == 0)
                    {
                        await botClient.SendTextMessageAsync(userId, "У вас нет напоминаний.");
                        return;
                    }
                    var editButtons = reminders.Select(r =>
                    {
                        var status = r.IsActive ? "🟢" : "⚫";
                        return InlineKeyboardButton.WithCallbackData(
                            $"{status} {r.DisplayOrder}. {r.Title}", $"edit_{r.Id}");
                    }).ToArray();

                    var editKeyboard = new InlineKeyboardMarkup(editButtons);
                    await botClient.SendTextMessageAsync(userId,
                        "Выберите напоминание для редактирования:",
                        replyMarkup: editKeyboard);
                    break;

                case "/delete_note":
                    var allReminders = await _reminderService.GetAllRemindersAsync(userId);
                    if (allReminders.Count == 0)
                    {
                        await botClient.SendTextMessageAsync(userId, "У вас нет напоминаний.");
                        return;
                    }
                    var deleteButtons = allReminders.Select(r =>
                        InlineKeyboardButton.WithCallbackData(
                            $"❌ {r.DisplayOrder}. {r.Title}", $"delete_{r.Id}")
                    ).ToArray();
                    var deleteKeyboard = new InlineKeyboardMarkup(deleteButtons);
                    await botClient.SendTextMessageAsync(userId,
                        "Выберите напоминание для удаления:",
                        replyMarkup: deleteKeyboard);
                    break;

                case "/all":
                    var allNotes = await _reminderService.GetAllRemindersAsync(userId);
                    if (allNotes.Count == 0)
                    {
                        await botClient.SendTextMessageAsync(userId, "📋 У вас нет напоминаний.");
                        return;
                    }
                    var msgText = "📋 <b>Ваши напоминания:</b>\n\n";
                    foreach (var r in allNotes)
                    {
                        var statusIcon = r.IsActive ? "🟢" : "⚫";
                        msgText += $"{statusIcon} <b>{r.DisplayOrder}. {EscapeHtml(r.Title)}</b>\n";
                        msgText += $"   Текст: {EscapeHtml(r.Text)}\n";
                        msgText += $"   Расписание: {GetScheduleDescription(r)}\n";

                        // Предварительные уведомления
                        if (!string.IsNullOrEmpty(r.PreNotificationOffsets))
                        {
                            var offsets = r.PreNotificationOffsets.Split(',')
                                .Select(o => int.Parse(o.Trim()))
                                .OrderByDescending(o => o)
                                .ToList();
                            var offsetStr = string.Join(", ", offsets.Select(FormatMinutes));
                            msgText += $"   🔔 Пред-уведомления: {offsetStr} до события\n";
                        }

                        // Время до срабатывания
                        if (r.IsActive && r.NextTriggerUtc.HasValue)
                        {
                            var userTZ = user.TimezoneOffsetHours;
                            var localTime = r.NextTriggerUtc.Value.AddHours(userTZ);
                            msgText += $"   Следующее: {localTime:dd.MM.yyyy HH:mm} (UTC{userTZ:+#;-#;0})\n";

                            var remaining = r.NextTriggerUtc.Value - DateTime.UtcNow;
                            if (remaining.TotalSeconds > 0)
                            {
                                var days = (int)remaining.TotalDays;
                                var hours = remaining.Hours;
                                var minutes = remaining.Minutes;
                                msgText += $"   ⏳ Осталось: Дней: {days}. Часов: {hours}. Минут: {minutes}.\n";
                            }
                            else
                            {
                                msgText += $"   ⏳ Осталось: < 1 мин.\n";
                            }
                        }
                        else if (!r.IsActive)
                        {
                            msgText += $"   Статус: ⚫ Неактивно\n";
                        }
                        msgText += "\n";
                    }
                    await botClient.SendTextMessageAsync(userId, msgText, parseMode: ParseMode.Html);
                    break;

                case "/timezone":
                    _userStates[userId] = (UserState.SettingTimezone, null);
                    await botClient.SendTextMessageAsync(userId,
                        $"🕐 Ваш текущий часовой пояс: UTC{user.TimezoneOffsetHours:+#;-#;0}\n" +
                        "Введите смещение (например, 3 для UTC+3 или -5 для UTC-5):");
                    break;
            }
        }

        private static async Task HandleInputAsync(ITelegramBotClient botClient, Message message, AppUser user, string? text)
        {
            if (text == null) return;
            var userId = user.TelegramId;
            var stateData = _userStates.GetValueOrDefault(userId);
            var state = stateData.State;
            var tempReminder = stateData.TempReminder;
            
            Console.WriteLine($"[Input] User {userId}, State: {state}, Text: {text}");

            // Проверка на недопустимые символы (кроме состояний, где нужны спецсимволы)
            if (state != UserState.EnteringTime && state != UserState.EditingSchedule && 
                state != UserState.EditingPreNotifications && state != UserState.SettingTimezone &&
                !IsValidInput(text))
            {
                await botClient.SendTextMessageAsync(userId, "⚠️ Допускаются только буквы, цифры и пробелы. Попробуйте снова.");
                return;
            }

            try
            {
                switch (state)
                {
                    // ==================== СОЗДАНИЕ НАПОМИНАНИЯ ====================
                    case UserState.EnteringTitle:
                        Console.WriteLine("[Input] Processing EnteringTitle");
                        tempReminder = new Reminder { Title = text, UserId = userId };
                        _userStates[userId] = (UserState.EnteringText, tempReminder);
                        await botClient.SendTextMessageAsync(userId, "📝 Введите текст напоминания:");
                        break;

                    case UserState.EnteringText:
                        Console.WriteLine("[Input] Processing EnteringText");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }
                        tempReminder.Text = text;
                        _userStates[userId] = (UserState.ChoosingSchedule, tempReminder);
                        
                        // Клавиатура выбора расписания (с добавленным вариантом "Один раз")
                        var scheduleKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("📅 Каждый день", "schedule_daily") },
                            new[] { InlineKeyboardButton.WithCallbackData("⏰ Интервал", "schedule_interval") },
                            new[] { InlineKeyboardButton.WithCallbackData("📆 По дням недели", "schedule_weekly") },
                            new[] { InlineKeyboardButton.WithCallbackData("🔹 Один раз", "schedule_onetime") }
                        });
                        await botClient.SendTextMessageAsync(userId, "Выберите тип расписания:", replyMarkup: scheduleKeyboard);
                        break;

                    case UserState.EnteringInterval:
                        Console.WriteLine("[Input] Processing EnteringInterval");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }
                        
                        int totalMinutes = 0;
                        if (text.Contains(':')) {
                            var parts = text.Split(':');
                            if (parts.Length == 2 && int.TryParse(parts[0], out var hrs) && int.TryParse(parts[1], out var mins) && hrs >= 0 && mins >= 0 && mins < 60)
                                totalMinutes = hrs * 60 + mins;
                        } else if (int.TryParse(text, out var minutes) && minutes > 0) {
                            totalMinutes = minutes;
                        }

                        if (totalMinutes <= 0) {
                            await botClient.SendTextMessageAsync(userId, "⚠️ Неверный формат. Введите число минут или Ч:ММ:");
                            return;
                        }
                        tempReminder.IntervalMinutes = totalMinutes;
                        tempReminder.Time = "00:00"; 
                        
                        var newIntervalReminder = await _reminderService.CreateReminderAsync(
                            userId, tempReminder.Title, tempReminder.Text, tempReminder.ScheduleType, 
                            tempReminder.IntervalHours, tempReminder.IntervalMinutes, tempReminder.WeekDays, tempReminder.Time, null ,user.TimezoneOffsetHours);
                        _userStates.Remove(userId);
                        await botClient.SendTextMessageAsync(userId, $"✅ Напоминание создано!\nБудет срабатывать каждые {totalMinutes} мин.");
                        break;

                    case UserState.EnteringWeekDays:
                        Console.WriteLine("[Input] Processing EnteringWeekDays");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }
                        if (!IsValidWeekDays(text)) {
                            await botClient.SendTextMessageAsync(userId, "⚠️ Введите дни через запятую (1-7). Например: 1,3,5");
                            return;
                        }
                        tempReminder.WeekDays = text;
                        _userStates[userId] = (UserState.EnteringTime, tempReminder);
                        await botClient.SendTextMessageAsync(userId, "🕐 Введите время в формате ЧЧ:ММ:");
                        break;

                    case UserState.EnteringTime:
                        Console.WriteLine("[Input] Processing EnteringTime");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }
                        
                        // Парсинг времени
                        var timeText = text.Trim();
                        if (!TimeSpan.TryParseExact(timeText, new[] { "hh\\:mm", "h\\:mm", "hh:mm", "h:mm" }, null, out var timeSpan)) {
                            var parts = timeText.Split(' ', ':');
                            if (parts.Length == 2 && int.TryParse(parts[0], out var th) && int.TryParse(parts[1], out var tm) && th >= 0 && th <= 23 && tm >= 0 && tm <= 59) {
                                timeSpan = new TimeSpan(th, tm, 0);
                            } else {
                                await botClient.SendTextMessageAsync(userId, "⚠️ Неверный формат времени. Используйте ЧЧ:ММ:");
                                return;
                            }
                        }
                        tempReminder.Time = $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}";

                        // ПРОВЕРКА: Создаем новое или редактируем существующее?
                        if (tempReminder.Id > 0) 
                        {
                            // РЕДАКТИРОВАНИЕ ВРЕМЕНИ
                            var userForEdit = await _reminderService.GetUserByIdAsync(userId);
                            tempReminder.NextTriggerUtc = CalculateNextTrigger(tempReminder.ScheduleType, tempReminder.IntervalHours, tempReminder.IntervalMinutes, tempReminder.WeekDays, tempReminder.Time, userForEdit?.TimezoneOffsetHours ?? 0);
                            tempReminder.NextPreNotificationIndex = 0;
                            _context.Reminders.Update(tempReminder);
                            await _context.SaveChangesAsync();
                            
                            _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                            await botClient.SendTextMessageAsync(userId, $"✅ Время обновлено: {tempReminder.Time}");
                            await SendEditMenuAsync(botClient, userId, tempReminder);
                        }
                        else 
                        {
                            // СОЗДАНИЕ НОВОГО
                            var newReminder = await _reminderService.CreateReminderAsync(
                                userId, tempReminder.Title, tempReminder.Text, tempReminder.ScheduleType, 
                                tempReminder.IntervalHours, tempReminder.IntervalMinutes, tempReminder.WeekDays, tempReminder.Time, null,user.TimezoneOffsetHours);
                            _userStates.Remove(userId);
                            await botClient.SendTextMessageAsync(userId, $"✅ Напоминание \"{newReminder.Title}\" создано!");
                        }
                        break;

                    // ==================== РЕДАКТИРОВАНИЕ НАПОМИНАНИЯ ====================
                    case UserState.EditingTitle:
                        Console.WriteLine("[Input] Processing EditingTitle");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }
                        tempReminder.Title = text;
                        _context.Reminders.Update(tempReminder);
                        await _context.SaveChangesAsync();
                        _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                        await botClient.SendTextMessageAsync(userId, $"✅ Название изменено на: \"{text}\"");
                        await SendEditMenuAsync(botClient, userId, tempReminder);
                        break;

                    case UserState.EditingText:
                        Console.WriteLine("[Input] Processing EditingText");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }
                        tempReminder.Text = text;
                        _context.Reminders.Update(tempReminder);
                        await _context.SaveChangesAsync();
                        _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                        await botClient.SendTextMessageAsync(userId, "✅ Текст изменён.");
                        await SendEditMenuAsync(botClient, userId, tempReminder);
                        break;

                    case UserState.EditingSchedule:
                        Console.WriteLine("[Input] Processing EditingSchedule");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }

                        switch (tempReminder.ScheduleType)
                        {
                            case "Daily":
                            case "OneTime":
                                // Парсим время (код аналогичен EnteringTime)
                                var editTimeText = text.Trim();
                                if (!TimeSpan.TryParseExact(editTimeText, new[] { "hh\\:mm", "h\\:mm", "hh:mm", "h:mm" }, null, out var editTimeSpan)) {
                                    var parts = editTimeText.Split(' ', ':');
                                    if (parts.Length == 2 && int.TryParse(parts[0], out var th) && int.TryParse(parts[1], out var tm) && th >= 0 && th <= 23 && tm >= 0 && tm <= 59) {
                                        editTimeSpan = new TimeSpan(th, tm, 0);
                                    } else {
                                        await botClient.SendTextMessageAsync(userId, "⚠️ Неверный формат. Используйте ЧЧ:ММ:");
                                        return;
                                    }
                                }
                                tempReminder.Time = $"{editTimeSpan.Hours:D2}:{editTimeSpan.Minutes:D2}";
                                var uEdit = await _reminderService.GetUserByIdAsync(userId);
                                tempReminder.NextTriggerUtc = CalculateNextTrigger(tempReminder.ScheduleType, null, null, null, tempReminder.Time, uEdit?.TimezoneOffsetHours ?? 0);
                                tempReminder.NextPreNotificationIndex = 0;
                                _context.Reminders.Update(tempReminder);
                                await _context.SaveChangesAsync();
                                _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                                await botClient.SendTextMessageAsync(userId, $"✅ Расписание обновлено. Время: {tempReminder.Time}");
                                await SendEditMenuAsync(botClient, userId, tempReminder);
                                break;

                            case "Interval":
                                int editTotalMinutes = 0;
                                if (text.Contains(':')) {
                                    var parts = text.Split(':');
                                    if (parts.Length == 2 && int.TryParse(parts[0], out var hrs) && int.TryParse(parts[1], out var mins) && hrs >= 0 && mins >= 0 && mins < 60)
                                        editTotalMinutes = hrs * 60 + mins;
                                } else if (int.TryParse(text, out var minutes) && minutes > 0) {
                                    editTotalMinutes = minutes;
                                }
                                if (editTotalMinutes <= 0) {
                                    await botClient.SendTextMessageAsync(userId, "⚠️ Неверный формат.");
                                    return;
                                }
                                tempReminder.IntervalMinutes = editTotalMinutes;
                                tempReminder.NextTriggerUtc = DateTime.UtcNow.AddMinutes(editTotalMinutes);
                                tempReminder.NextPreNotificationIndex = 0;
                                _context.Reminders.Update(tempReminder);
                                await _context.SaveChangesAsync();
                                _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                                await botClient.SendTextMessageAsync(userId, $"✅ Интервал обновлён: каждые {editTotalMinutes} мин.");
                                await SendEditMenuAsync(botClient, userId, tempReminder);
                                break;

                            case "Weekly":
                                if (!IsValidWeekDays(text)) {
                                    await botClient.SendTextMessageAsync(userId, "⚠️ Введите дни через запятую (1-7):");
                                    return;
                                }
                                tempReminder.WeekDays = text;
                                // После ввода дней просим ввести время, переходим в состояние EnteringTime
                                // Так как Id > 0, метод EnteringTime поймет, что это редактирование
                                _userStates[userId] = (UserState.EnteringTime, tempReminder);
                                await botClient.SendTextMessageAsync(userId, "🕐 Введите время в формате ЧЧ:ММ:");
                                break;
                        }
                        break;

                    case UserState.EditingPreNotifications:
                        Console.WriteLine("[Input] Processing EditingPreNotifications");
                        if (tempReminder == null) { await botClient.SendTextMessageAsync(userId, "⚠️ Ошибка сессии."); return; }

                        if (text.Trim() == "0") {
                            await _reminderService.UpdatePreNotificationsAsync(userId, tempReminder.Id, null);
                            _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                            await botClient.SendTextMessageAsync(userId, "✅ Пред-уведомления отключены.");
                            await SendEditMenuAsync(botClient, userId, tempReminder);
                        } else {
                            var offsetParts = text.Split(',');
                            var validOffsets = new List<int>();
                            bool allValid = true;
                            foreach (var part in offsetParts) {
                                if (int.TryParse(part.Trim(), out var offset) && offset > 0) validOffsets.Add(offset);
                                else { allValid = false; break; }
                            }
                            if (!allValid || validOffsets.Count == 0) {
                                await botClient.SendTextMessageAsync(userId, "⚠️ Введите числа через запятую (например: 1440,60,15) или 0:");
                                return;
                            }
                            var offsetsStr = string.Join(",", validOffsets.OrderByDescending(o => o));
                            await _reminderService.UpdatePreNotificationsAsync(userId, tempReminder.Id, offsetsStr);
                            _userStates[userId] = (UserState.EditingReminderMenu, tempReminder);
                            await botClient.SendTextMessageAsync(userId, $"✅ Пред-уведомления установлены: {offsetsStr} мин.");
                            await SendEditMenuAsync(botClient, userId, tempReminder);
                        }
                        break;

                    // ==================== ЧАСОВОЙ ПОЯС ====================
                    case UserState.SettingTimezone:
                        Console.WriteLine("[Input] Processing SettingTimezone");
                        var tzText = text.Trim().Replace("+", "");
                        if (int.TryParse(tzText, out var tzOffset) && tzOffset >= -12 && tzOffset <= 14) {
                            user.TimezoneOffsetHours = tzOffset;
                            await _context.SaveChangesAsync();
                            _userStates.Remove(userId);
                            await botClient.SendTextMessageAsync(userId, $"✅ Часовой пояс установлен: UTC{tzOffset:+#;-#;0}");
                        } else {
                            await botClient.SendTextMessageAsync(userId, "❌ Неверный формат. Введите число от -12 до 14:");
                        }
                        break;

                    // ==================== ЗАГЛУШКИ ====================
                    case UserState.DeletingReminder:
                    case UserState.EditingReminderMenu:
                    case UserState.ChoosingSchedule:
                        Console.WriteLine($"[Input] State {state} - waiting for callback/button");
                        break;

                    default:
                        if (state != UserState.Idle) Console.WriteLine($"[Input] Unknown state: {state}");
                        await botClient.SendTextMessageAsync(userId, "Используйте команды: /create_note, /edit_note, /delete_note, /all");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Input Error] {ex.Message}");
                await botClient.SendTextMessageAsync(userId, $"⚠️ Произошла ошибка: {ex.Message}");
            }
        }

        private static async Task CheckRemindersLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var dueReminders = await _reminderService.GetDueRemindersAsync();
                    foreach (var reminder in dueReminders)
                    {
                        bool isPreNotification = false;

                        // Проверяем, это пред-уведомление или основное событие
                        if (!string.IsNullOrEmpty(reminder.PreNotificationOffsets) && reminder.NextPreNotificationIndex >= 0)
                        {
                            var offsets = reminder.PreNotificationOffsets.Split(',')
                                .Select(o => int.Parse(o.Trim()))
                                .OrderByDescending(o => o)
                                .ToList();

                            if (reminder.NextPreNotificationIndex < offsets.Count)
                            {
                                var offset = offsets[reminder.NextPreNotificationIndex];
                                var preTime = reminder.NextTriggerUtc?.AddMinutes(-offset);
                                if (preTime.HasValue && preTime.Value <= DateTime.UtcNow)
                                {
                                    isPreNotification = true;
                                    var offsetStr = FormatMinutes(offset);
                                    await _botClient.SendTextMessageAsync(
                                        reminder.UserId,
                                        $"🔔 <b>Предварительное уведомление</b> (за {offsetStr})\n\n" +
                                        $"⏰ <b>{EscapeHtml(reminder.Title)}</b>\n\n{EscapeHtml(reminder.Text)}",
                                        parseMode: ParseMode.Html);

                                    reminder.NextPreNotificationIndex++;
                                    await _reminderService.UpdateReminderAsync(reminder);
                                    continue; // Не обрабатываем основное событие в этой итерации
                                }
                            }
                        }

                        // Основное событие
                        if (!isPreNotification && reminder.NextTriggerUtc.HasValue &&
                            reminder.NextTriggerUtc.Value <= DateTime.UtcNow)
                        {
                            await _botClient.SendTextMessageAsync(
                                reminder.UserId,
                                $"⏰ <b>Напоминание: {EscapeHtml(reminder.Title)}</b>\n\n{EscapeHtml(reminder.Text)}",
                                parseMode: ParseMode.Html);

                            if (reminder.ScheduleType == "OneTime")
                            {
                                reminder.IsActive = false;
                                _context.Reminders.Update(reminder);
                                await _context.SaveChangesAsync();
                            }
                            else
                            {
                                var user = await _reminderService.GetUserByIdAsync(reminder.UserId);
                                reminder.NextTriggerUtc = CalculateNextTrigger(
                                    reminder.ScheduleType, reminder.IntervalHours,
                                    reminder.IntervalMinutes, reminder.WeekDays, reminder.Time,
                                    user?.TimezoneOffsetHours ?? 0);
                                reminder.NextPreNotificationIndex = 0;
                                await _reminderService.UpdateReminderAsync(reminder);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при проверке напоминаний: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }

        private static DateTime? CalculateNextTrigger(string scheduleType, int? intervalHours, 
            int? intervalMinutes, string? weekDays, string time, int timezoneOffsetHours = 0)
        {
            var now = DateTime.UtcNow;
            if (!TimeSpan.TryParse(time, out var timeSpan))
                return null;
            
            // Создаем время с учетом часового пояса пользователя
            var userLocalTime = now.AddHours(timezoneOffsetHours);
            var nextTrigger = userLocalTime.Date.Add(timeSpan).AddHours(-timezoneOffsetHours);
            
            if (nextTrigger <= now)
                nextTrigger = nextTrigger.AddDays(1);
            
            switch (scheduleType)
            {
                case "Daily":
                    return nextTrigger;
                case "Interval":
                    if (intervalMinutes.HasValue)
                        return now.AddMinutes(intervalMinutes.Value);
                    return null;
                case "Weekly":
                    if (!string.IsNullOrEmpty(weekDays))
                    {
                        var days = weekDays.Split(',')
                            .Select(d => int.Parse(d.Trim()))
                            .ToList();
                        // Проверяем дни недели с учетом часового пояса
                        while (!days.Contains((int)nextTrigger.AddHours(timezoneOffsetHours).DayOfWeek == 0 ? 7 : (int)nextTrigger.AddHours(timezoneOffsetHours).DayOfWeek))
                        {
                            nextTrigger = nextTrigger.AddDays(1);
                        }
                        return nextTrigger;
                    }
                    return null;
                case "OneTime":  // ← ДОБАВЬТЕ ЭТОТ БЛОК
                    return nextTrigger;
                default:
                    return null;
            }
        }

        private static bool IsValidInput(string text)
        {
            return text.All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c));
        }

        private static bool IsValidWeekDays(string text)
        {
            var parts = text.Split(',');
            foreach (var part in parts)
            {
                if (!int.TryParse(part.Trim(), out var day) || day < 1 || day > 7)
                    return false;
            }
            return true;
        }


        private static string GetDayName(int day)
        {
            return day switch
            {
                1 => "Пн",
                2 => "Вт",
                3 => "Ср",
                4 => "Чт",
                5 => "Пт",
                6 => "Сб",
                7 => "Вс",
                _ => "?"
            };
        }

        private static string EscapeHtml(string text)
        {
            return text.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;");
        }

        private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
        {
            Console.WriteLine($"Ошибка: {exception.Message}");
            return Task.CompletedTask;
        }
        // Метод для загрузки переменных из .env файла
        private static void LoadEnvFile()
        {
            // Используем полные имена, чтобы избежать конфликта с Telegram.Bot.Types.File
            var envPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), ".env");
            
            if (System.IO.File.Exists(envPath))
            {
                string[] lines = System.IO.File.ReadAllLines(envPath);
                foreach (string line in lines)
                {
                    // Пропускаем пустые строки и комментарии
                    if (string.IsNullOrWhiteSpace(line) || line.Trim().StartsWith("#"))
                        continue;
                    
                    // Разделяем строку по первому знаку '=' максимум на 2 части
                    string[] parts = line.Split(new char[] { '=' }, 2);
                    
                    if (parts.Length == 2)
                    {
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }
                Console.WriteLine("✅ Переменные окружения загружены из .env");
            }
            else
            {
                Console.WriteLine("⚠️ Файл .env не найден. Используются системные переменные окружения.");
            }
        }

        private static string FormatMinutes(int minutes)
        {
            if (minutes >= 1440 && minutes % 1440 == 0)
                return $"{minutes / 1440} дн.";
            if (minutes >= 60 && minutes % 60 == 0)
                return $"{minutes / 60} ч.";
            if (minutes >= 60)
                return $"{minutes / 60} ч. {minutes % 60} мин.";
            return $"{minutes} мин.";
        }

        private static string GetScheduleDescription(Reminder reminder)
        {
            switch (reminder.ScheduleType)
            {
                case "Daily":
                    return $"Каждый день в {reminder.Time}";
                case "Interval":
                    if (reminder.IntervalMinutes.HasValue)
                    {
                        var mins = reminder.IntervalMinutes.Value;
                        return $"Каждые {FormatMinutes(mins)}";
                    }
                    return "Каждые N мин.";
                case "Weekly":
                    var days = reminder.WeekDays?.Split(',')
                        .Select(d => GetDayName(int.Parse(d.Trim())))
                        .ToList();
                    return $"По {string.Join(", ", days ?? new List<string>())} в {reminder.Time}";
                case "OneTime":
                    return $"Один раз в {reminder.Time}";
                default:
                    return "Неизвестно";
            }
        }
    }
}
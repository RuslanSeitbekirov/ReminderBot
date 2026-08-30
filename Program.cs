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

        static async Task Main(string[] args)
        {
            using var context = new AppDbContext();
            await context.Database.EnsureCreatedAsync();
            _reminderService = new ReminderService(context);

            var botToken = "8825595358:AAEaDfFAjo7yZNw2AGGQ-o-Ykp2VZNKrC1c";
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
            Console.WriteLine($"[Callback] Current state: {_userStates.GetValueOrDefault(userId).State}");

            try
            {
                if (data.StartsWith("schedule_"))
                {
                    var scheduleType = data.Replace("schedule_", "");

                    scheduleType = char.ToUpper(scheduleType[0]) + scheduleType.Substring(1);

                    var stateData = _userStates.GetValueOrDefault(userId);
                    var tempReminder = stateData.TempReminder;
                    
                    Console.WriteLine($"[Callback] TempReminder is null: {tempReminder == null}");
                    
                    if (tempReminder == null) 
                    {
                        await botClient.SendTextMessageAsync(userId, 
                            "️⚠️ Ошибка: сессия создания прервана. Пожалуйста, начните заново с /create_note");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    tempReminder.ScheduleType = scheduleType;
                    Console.WriteLine($"[Callback] Set schedule type: {scheduleType}");

                    switch (scheduleType)
                    {
                        case "Daily":
                            _userStates[userId] = (UserState.EnteringTime, tempReminder);
                            await botClient.SendTextMessageAsync(userId, 
                                "🕐 Введите время в формате ЧЧ:ММ (например, 14:30):");
                            break;

                        case "Hourly":
                            _userStates[userId] = (UserState.EnteringInterval, tempReminder);
                            await botClient.SendTextMessageAsync(userId, 
                                "⏰ Введите интервал в часах (например, 6):");
                            break;

                        case "Weekly":
                            _userStates[userId] = (UserState.EnteringWeekDays, tempReminder);
                            await botClient.SendTextMessageAsync(userId, 
                                "📆 Введите дни недели через запятую (1=Пн, 2=Вт, ..., 7=Вс).\n" +
                                "Например: 1,3,5 (понедельник, среда, пятница):");
                            break;
                    }
                    
                    Console.WriteLine($"[Callback] New state: {_userStates[userId].State}");
                }
                else if (data.StartsWith("edit_schedule_"))
                {
                    var scheduleType = data.Replace("edit_schedule_", "");
                    var stateData = _userStates.GetValueOrDefault(userId);
                    var tempReminder = stateData.TempReminder;
                    
                    if (tempReminder == null) 
                    {
                        await botClient.SendTextMessageAsync(userId, 
                            "⚠️ Ошибка: сессия редактирования прервана.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    tempReminder.ScheduleType = scheduleType;

                    switch (scheduleType)
                    {
                        case "Daily":
                            _userStates[userId] = (UserState.EnteringTime, tempReminder);
                            await botClient.SendTextMessageAsync(userId, 
                                " Введите новое время в формате ЧЧ:ММ:");
                            break;

                        case "Hourly":
                            _userStates[userId] = (UserState.EnteringInterval, tempReminder);
                            await botClient.SendTextMessageAsync(userId, 
                                " Введите новый интервал в часах:");
                            break;

                        case "Weekly":
                            _userStates[userId] = (UserState.EnteringWeekDays, tempReminder);
                            await botClient.SendTextMessageAsync(userId, 
                                "📆 Введите новые дни недели через запятую (1=Пн, ..., 7=Вс):");
                            break;
                    }
                }
                else if (data.StartsWith("edit_"))
                {
                    var reminderId = int.Parse(data.Replace("edit_", ""));
                    var reminder = await _reminderService.GetReminderByIdAsync(userId, reminderId);
                    
                    if (reminder == null)
                    {
                        await botClient.SendTextMessageAsync(userId, "Напоминание не найдено.");
                        await botClient.AnswerCallbackQueryAsync(callback.Id);
                        return;
                    }

                    _userStates[userId] = (UserState.EditingTitle, reminder);
                    await botClient.SendTextMessageAsync(userId, 
                        $"️ Редактирование: \"{reminder.Title}\"\n\n" +
                        "Введите новое название:");
                }
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
                        await botClient.SendTextMessageAsync(userId, "✅ Напоминание удалено.");
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
                    $"️ Произошла ошибка: {ex.Message}\nПопробуйте начать заново.");
            }

            await botClient.AnswerCallbackQueryAsync(callback.Id);
        }
        private static async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, AppUser user, string command)
        {
            var userId = user.TelegramId;

            switch (command)
            {
                case "/create_note":
                    _userStates[userId] = (UserState.EnteringTitle, null);
                    await botClient.SendTextMessageAsync(userId, 
                        " Введите название напоминания (только текст и цифры):");
                    break;

                case "/edit_note":
                    var reminders = await _reminderService.GetAllRemindersAsync(userId);
                    if (reminders.Count == 0)
                    {
                        await botClient.SendTextMessageAsync(userId, "У вас нет активных напоминаний.");
                        return;
                    }

                    var keyboard = new InlineKeyboardMarkup(
                        reminders.Select(r => InlineKeyboardButton.WithCallbackData(
                            $"{r.Id}. {r.Title}", $"edit_{r.Id}"))
                    );
                    await botClient.SendTextMessageAsync(userId, "Выберите напоминание для редактирования:", 
                        replyMarkup: keyboard);
                    break;

                case "/delete_note":
                    var allReminders = await _reminderService.GetAllRemindersAsync(userId);
                    if (allReminders.Count == 0)
                    {
                        await botClient.SendTextMessageAsync(userId, "У вас нет активных напоминаний.");
                        return;
                    }

                    var deleteKeyboard = new InlineKeyboardMarkup(
                        allReminders.Select(r => InlineKeyboardButton.WithCallbackData(
                            $"❌ {r.Id}. {r.Title}", $"delete_{r.Id}"))
                    );
                    await botClient.SendTextMessageAsync(userId, "Выберите напоминание для удаления:", 
                        replyMarkup: deleteKeyboard);
                    break;

                case "/all":
                    var allNotes = await _reminderService.GetAllRemindersAsync(userId);
                    if (allNotes.Count == 0)
                    {
                        await botClient.SendTextMessageAsync(userId, "📋 У вас нет напоминаний.");
                        return;
                    }

                    var messageText = "📋 <b>Ваши напоминания:</b>\n\n";
                    foreach (var r in allNotes)
                    {
                        messageText += $"<b>{r.Id}. {EscapeHtml(r.Title)}</b>\n";
                        messageText += $"   Текст: {EscapeHtml(r.Text)}\n";
                        messageText += $"   Расписание: {GetScheduleDescription(r)}\n";
                        messageText += $"   Следующее: {r.NextTriggerUtc?.ToLocalTime():dd.MM.yyyy HH:mm}\n\n";
                    }

                    await botClient.SendTextMessageAsync(userId, messageText, parseMode: ParseMode.Html);
                    break;

                default:
                    await botClient.SendTextMessageAsync(userId, "Неизвестная команда.");
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

            if (state != UserState.EnteringTime && !IsValidInput(text))
            {
                await botClient.SendTextMessageAsync(userId, 
                    "⚠️ Допускаются только буквы, цифры и пробелы. Попробуйте снова.");
                return;
            }

            try
            {
                switch (state)
                {
                    case UserState.EnteringTitle:
                        Console.WriteLine("[Input] Processing EnteringTitle");
                        tempReminder = new Reminder { Title = text, UserId = userId };
                        _userStates[userId] = (UserState.EnteringText, tempReminder);
                        await botClient.SendTextMessageAsync(userId, 
                            " Введите текст напоминания:");
                        break;

                    case UserState.EnteringText:
                        Console.WriteLine("[Input] Processing EnteringText");
                        if (tempReminder == null) 
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "⚠️ Ошибка сессии. Начните заново с /create_note");
                            return;
                        }
                        tempReminder.Text = text;
                        _userStates[userId] = (UserState.ChoosingSchedule, tempReminder);
                        
                        var scheduleKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("📅 Каждый день", "schedule_daily") },
                            new[] { InlineKeyboardButton.WithCallbackData("⏰ Каждые N часов", "schedule_hourly") },
                            new[] { InlineKeyboardButton.WithCallbackData(" В определенные дни", "schedule_weekly") }
                        });
                        await botClient.SendTextMessageAsync(userId, 
                            "Выберите тип расписания:", replyMarkup: scheduleKeyboard);
                        break;

                    case UserState.EnteringInterval:
                        Console.WriteLine("[Input] Processing EnteringInterval");
                        if (tempReminder == null) 
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "️ Ошибка сессии. Начните заново с /create_note");
                            return;
                        }
                        if (!int.TryParse(text, out var hours) || hours <= 0)
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "⚠️ Введите положительное число (количество часов):");
                            return;
                        }
                        tempReminder.IntervalHours = hours;
                        _userStates[userId] = (UserState.EnteringTime, tempReminder);
                        await botClient.SendTextMessageAsync(userId, 
                            " Введите время в формате ЧЧ:ММ (например, 14:30):");
                        break;

                    case UserState.EnteringWeekDays:
                        Console.WriteLine("[Input] Processing EnteringWeekDays");
                        if (tempReminder == null) 
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "⚠️ Ошибка сессии. Начните заново с /create_note");
                            return;
                        }
                        if (!IsValidWeekDays(text))
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "⚠️ Введите дни недели через запятую (1=Пн, 2=Вт, ..., 7=Вс). Например: 1,3,5");
                            return;
                        }
                        tempReminder.WeekDays = text;
                        _userStates[userId] = (UserState.EnteringTime, tempReminder);
                        await botClient.SendTextMessageAsync(userId, 
                            " Введите время в формате ЧЧ:ММ (например, 14:30):");
                        break;

                        case UserState.EnteringTime:
                            Console.WriteLine("[Input] Processing EnteringTime");
                            if (tempReminder == null) 
                            {
                                await botClient.SendTextMessageAsync(userId, 
                                    "⚠️ Ошибка сессии. Начните заново с /create_note");
                                return;
                            }
                            
                            // Гибкий парсинг времени
                            var timeText = text.Trim();
                            if (!TimeSpan.TryParseExact(timeText, new[] { "hh\\:mm", "h\\:mm", "hh:mm", "h:mm" }, 
                                null, out var timeSpan))
                            {
                                // Пробуем преобразовать "18 52" -> "18:52"
                                var parts = timeText.Split(' ', ':');
                                if (parts.Length == 2 && 
                                    int.TryParse(parts[0], out var timeHours) && 
                                    int.TryParse(parts[1], out var timeMinutes) &&
                                    timeHours >= 0 && timeHours <= 23 && 
                                    timeMinutes >= 0 && timeMinutes <= 59)
                                {
                                    timeSpan = new TimeSpan(timeHours, timeMinutes, 0);
                                }
                                else
                                {
                                    await botClient.SendTextMessageAsync(userId, 
                                        "⚠️ Неверный формат времени. Используйте ЧЧ:ММ (например, 14:30 или 14 30):");
                                    return;
                                }
                            }
                            
                            tempReminder.Time = $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}";
                        
                        Console.WriteLine($"[Input] Creating reminder: {tempReminder.Title}, {tempReminder.ScheduleType}");
                        
                        var reminder = await _reminderService.CreateReminderAsync(
                            userId, tempReminder.Title, tempReminder.Text,
                            tempReminder.ScheduleType, tempReminder.IntervalHours,
                            tempReminder.WeekDays, tempReminder.Time);

                        _userStates.Remove(userId);
                        await botClient.SendTextMessageAsync(userId, 
                            $"✅ Напоминание \"{reminder.Title}\" создано!\n" +
                            $"Следующее срабатывание: {reminder.NextTriggerUtc?.ToLocalTime():dd.MM.yyyy HH:mm}");
                        break;

                    case UserState.EditingTitle:
                        Console.WriteLine("[Input] Processing EditingTitle");
                        if (tempReminder == null) 
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "⚠️ Ошибка сессии редактирования.");
                            return;
                        }
                        tempReminder.Title = text;
                        _userStates[userId] = (UserState.EditingText, tempReminder);
                        await botClient.SendTextMessageAsync(userId, "📄 Введите новый текст напоминания:");
                        break;

                    case UserState.EditingText:
                        Console.WriteLine("[Input] Processing EditingText");
                        if (tempReminder == null) 
                        {
                            await botClient.SendTextMessageAsync(userId, 
                                "️ Ошибка сессии редактирования.");
                            return;
                        }
                        tempReminder.Text = text;
                        _userStates[userId] = (UserState.EditingSchedule, tempReminder);
                        
                        var editScheduleKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("📅 Каждый день", "edit_schedule_daily") },
                            new[] { InlineKeyboardButton.WithCallbackData("⏰ Каждые N часов", "edit_schedule_hourly") },
                            new[] { InlineKeyboardButton.WithCallbackData(" В определенные дни", "edit_schedule_weekly") }
                        });
                        await botClient.SendTextMessageAsync(userId, 
                            "Выберите новое расписание:", replyMarkup: editScheduleKeyboard);
                        break;

                    case UserState.EditingSchedule:
                        // Этот шаг обрабатывается через callback-кнопки
                        Console.WriteLine("[Input] EditingSchedule - waiting for callback");
                        break;

                    case UserState.DeletingReminder:
                        Console.WriteLine("[Input] DeletingReminder - waiting for callback");
                        break;

                    default:
                        if (state != UserState.Idle)
                        {
                            Console.WriteLine($"[Input] Unknown state: {state}");
                        }
                        await botClient.SendTextMessageAsync(userId, 
                            "Используйте команды: /create_note, /edit_note, /delete_note, /all");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Input Error] {ex.Message}");
                await botClient.SendTextMessageAsync(userId, 
                    $"⚠️ Произошла ошибка: {ex.Message}");
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
                        await _botClient.SendTextMessageAsync(
                        reminder.UserId,
                        $"⏰ <b>Напоминание: {EscapeHtml(reminder.Title)}</b>\n\n{EscapeHtml(reminder.Text)}",
                        parseMode: ParseMode.Html  // ← Именованный параметр
                        );

                        reminder.NextTriggerUtc = CalculateNextTrigger(
                            reminder.ScheduleType, reminder.IntervalHours, 
                            reminder.WeekDays, reminder.Time);
                        
                        await _reminderService.UpdateReminderAsync(reminder);
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
            string? weekDays, string time)
        {
            var now = DateTime.UtcNow;
            
            if (!TimeSpan.TryParse(time, out var timeSpan))
                return null;

            var nextTrigger = now.Date.Add(timeSpan);
            if (nextTrigger <= now)
                nextTrigger = nextTrigger.AddDays(1);

            switch (scheduleType)
            {
                case "Daily":
                    return nextTrigger;
                
                case "Hourly":
                    if (intervalHours.HasValue)
                        return now.AddHours(intervalHours.Value);
                    return null;
                
                case "Weekly":
                    if (!string.IsNullOrEmpty(weekDays))
                    {
                        var days = weekDays.Split(',')
                            .Select(d => int.Parse(d.Trim()))
                            .ToList();
                        
                        while (!days.Contains((int)nextTrigger.DayOfWeek == 0 ? 7 : (int)nextTrigger.DayOfWeek))
                        {
                            nextTrigger = nextTrigger.AddDays(1);
                        }
                        return nextTrigger;
                    }
                    return null;
                
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

        private static string GetScheduleDescription(Reminder reminder)
        {
            switch (reminder.ScheduleType)
            {
                case "Daily":
                    return $"Каждый день в {reminder.Time}";
                case "Hourly":
                    return $"Каждые {reminder.IntervalHours} ч. в {reminder.Time}";
                case "Weekly":
                    var days = reminder.WeekDays?.Split(',')
                        .Select(d => GetDayName(int.Parse(d.Trim())))
                        .ToList();
                    return $"По {string.Join(", ", days ?? new List<string>())} в {reminder.Time}";
                default:
                    return "Неизвестно";
            }
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
    }
}
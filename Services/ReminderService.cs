using Microsoft.EntityFrameworkCore;
using ReminderBot.Data;
using ReminderBot.Models;

namespace ReminderBot.Services
{
    public class ReminderService
    {
        private readonly AppDbContext _context;

        public ReminderService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<AppUser?> GetOrCreateUserAsync(long telegramId, string? username)
        {
            var user = await _context.Users.FindAsync(telegramId);
            if (user == null)
            {
                user = new AppUser { TelegramId = telegramId, Username = username };
                _context.Users.Add(user);
                await _context.SaveChangesAsync(); // ← Явное сохранение
            }
            return user;
        }

        public async Task<AppUser?> GetUserByIdAsync(long telegramId)
        {
            return await _context.Users.FindAsync(telegramId);
        }

        public async Task<Reminder?> CreateReminderAsync(long userId, string title, string text,
            string scheduleType, int? intervalHours, int? intervalMinutes, string? weekDays, string time,
            string? preNotificationOffsets = null, int timezoneOffsetHours = 0)
        {
            // Вычисляем следующий порядковый номер для этого пользователя
            var maxOrder = await _context.Reminders
                .Where(r => r.UserId == userId)
                .MaxAsync(r => (int?)r.DisplayOrder) ?? 0;

            var reminder = new Reminder
            {
                UserId = userId,
                Title = title,
                Text = text,
                ScheduleType = scheduleType,
                IntervalHours = intervalHours,
                IntervalMinutes = intervalMinutes,
                WeekDays = weekDays,
                Time = time,
                DisplayOrder = maxOrder + 1,
                PreNotificationOffsets = preNotificationOffsets,
                NextPreNotificationIndex = 0,
                NextTriggerUtc = CalculateNextTrigger(scheduleType, intervalHours,
                    intervalMinutes, weekDays, time, timezoneOffsetHours)
            };

            _context.Reminders.Add(reminder);
            await _context.SaveChangesAsync();
            return reminder;
        }

        // Возвращаем ВСЕ напоминания (включая неактивные)
        public async Task<List<Reminder>> GetAllRemindersAsync(long userId)
        {
            return await _context.Reminders
                .Where(r => r.UserId == userId)
                .OrderBy(r => r.DisplayOrder)
                .ToListAsync();
        }

        public async Task<Reminder?> GetReminderByIdAsync(long userId, int reminderId)
        {
            return await _context.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.UserId == userId);
        }

        public async Task<bool> UpdateReminderAsync(Reminder reminder)
        {
            var user = await GetUserByIdAsync(reminder.UserId);
            if (reminder.IsActive && reminder.ScheduleType != "OneTime")
            {
                reminder.NextTriggerUtc = CalculateNextTrigger(
                    reminder.ScheduleType, reminder.IntervalHours,
                    reminder.IntervalMinutes, reminder.WeekDays, reminder.Time,
                    user?.TimezoneOffsetHours ?? 0);
                reminder.NextPreNotificationIndex = 0;
            }
            _context.Reminders.Update(reminder);
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<bool> DeleteReminderAsync(long userId, int reminderId)
        {
            var reminder = await _context.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.UserId == userId);
            if (reminder == null) return false;

            var deletedOrder = reminder.DisplayOrder;
            _context.Reminders.Remove(reminder);
            await _context.SaveChangesAsync();

            // Перенумеруем последующие напоминания
            var subsequent = await _context.Reminders
                .Where(r => r.UserId == userId && r.DisplayOrder > deletedOrder)
                .OrderBy(r => r.DisplayOrder)
                .ToListAsync();

            foreach (var r in subsequent)
            {
                r.DisplayOrder--;
            }

            if (subsequent.Count > 0)
                await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> ToggleReminderStatusAsync(long userId, int reminderId)
        {
            var reminder = await _context.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.UserId == userId);
            if (reminder == null) return false;

            reminder.IsActive = !reminder.IsActive;
            
            if (reminder.IsActive)
            {
                var user = await GetUserByIdAsync(userId);
                reminder.NextTriggerUtc = CalculateNextTrigger(
                    reminder.ScheduleType, reminder.IntervalHours,
                    reminder.IntervalMinutes, reminder.WeekDays, reminder.Time,
                    user?.TimezoneOffsetHours ?? 0);
                reminder.NextPreNotificationIndex = 0;
            }
            
            _context.Reminders.Update(reminder);
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<bool> UpdatePreNotificationsAsync(long userId, int reminderId, string? offsets)
        {
            var reminder = await _context.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.UserId == userId);
            if (reminder == null) return false;

            reminder.PreNotificationOffsets = offsets;
            reminder.NextPreNotificationIndex = 0;
            
            _context.Reminders.Update(reminder);
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<List<Reminder>> GetDueRemindersAsync()
        {
            var now = DateTime.UtcNow;
            var reminders = await _context.Reminders
                .Where(r => r.IsActive)
                .Include(r => r.User)
                .ToListAsync();

            var due = new List<Reminder>();

            foreach (var r in reminders)
            {
                // Проверяем предварительные уведомления
                if (!string.IsNullOrEmpty(r.PreNotificationOffsets) && r.NextPreNotificationIndex >= 0)
                {
                    var offsets = r.PreNotificationOffsets.Split(',')
                        .Select(o => int.Parse(o.Trim()))
                        .OrderByDescending(o => o)
                        .ToList();

                    if (r.NextPreNotificationIndex < offsets.Count && r.NextTriggerUtc.HasValue)
                    {
                        var offset = offsets[r.NextPreNotificationIndex];
                        var preTime = r.NextTriggerUtc.Value.AddMinutes(-offset);
                        if (preTime <= now)
                        {
                            due.Add(r);
                            continue; // Не проверяем основное событие
                        }
                    }
                }

                // Проверяем основное событие
                if (r.NextTriggerUtc.HasValue && r.NextTriggerUtc.Value <= now)
                {
                    due.Add(r);
                }
            }

            return due;
        }

        private DateTime? CalculateNextTrigger(string scheduleType, int? intervalHours,
            int? intervalMinutes, string? weekDays, string time, int timezoneOffsetHours = 0)
        {
            var now = DateTime.UtcNow;
            if (!TimeSpan.TryParse(time, out var timeSpan))
                return null;

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
                        while (!days.Contains(
                            (int)nextTrigger.AddHours(timezoneOffsetHours).DayOfWeek == 0
                                ? 7
                                : (int)nextTrigger.AddHours(timezoneOffsetHours).DayOfWeek))
                        {
                            nextTrigger = nextTrigger.AddDays(1);
                        }
                        return nextTrigger;
                    }
                    return null;
                case "OneTime":
                    return nextTrigger;
                default:
                    return null;
            }
        }
    }
}
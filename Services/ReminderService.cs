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
                await _context.SaveChangesAsync();
            }
            return user;
        }

        public async Task<Reminder?> CreateReminderAsync(long userId, string title, string text, 
            string scheduleType, int? intervalHours, string? weekDays, string time)
        {
            var reminder = new Reminder
            {
                UserId = userId,
                Title = title,
                Text = text,
                ScheduleType = scheduleType,
                IntervalHours = intervalHours,
                WeekDays = weekDays,
                Time = time,
                NextTriggerUtc = CalculateNextTrigger(scheduleType, intervalHours, weekDays, time)
            };

            _context.Reminders.Add(reminder);
            await _context.SaveChangesAsync();
            return reminder;
        }

        public async Task<List<Reminder>> GetAllRemindersAsync(long userId)
        {
            return await _context.Reminders
                .Where(r => r.UserId == userId && r.IsActive)
                .OrderBy(r => r.NextTriggerUtc)
                .ToListAsync();
        }

        public async Task<Reminder?> GetReminderByIdAsync(long userId, int reminderId)
        {
            return await _context.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.UserId == userId);
        }

        public async Task<bool> UpdateReminderAsync(Reminder reminder)
        {
            reminder.NextTriggerUtc = CalculateNextTrigger(
                reminder.ScheduleType, reminder.IntervalHours, reminder.WeekDays, reminder.Time);
            
            _context.Reminders.Update(reminder);
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<bool> DeleteReminderAsync(long userId, int reminderId)
        {
            var reminder = await _context.Reminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.UserId == userId);
            
            if (reminder == null) return false;

            reminder.IsActive = false;
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<List<Reminder>> GetDueRemindersAsync()
        {
            var now = DateTime.UtcNow;
            return await _context.Reminders
                .Where(r => r.IsActive && r.NextTriggerUtc <= now)
                .Include(r => r.User)
                .ToListAsync();
        }

        private DateTime? CalculateNextTrigger(string scheduleType, int? intervalHours, 
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
                    {
                        nextTrigger = now.AddHours(intervalHours.Value);
                        return nextTrigger;
                    }
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
    }
}
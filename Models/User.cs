using System.ComponentModel.DataAnnotations;

namespace ReminderBot.Models
{
    public class AppUser
    {
        [Key]
        public long TelegramId { get; set; }
        public string? Username { get; set; }
        public int TimezoneOffsetHours { get; set; } = 0;
        public List<Reminder> Reminders { get; set; } = new();
    }
}
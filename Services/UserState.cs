namespace ReminderBot.Services
{
    public class UserState
    {
        // Шаги создания напоминания
        public const int Idle = 0;
        public const int EnteringTitle = 1;
        public const int EnteringText = 2;
        public const int ChoosingSchedule = 3;
        public const int EnteringInterval = 4;
        public const int EnteringWeekDays = 5;
        public const int EnteringTime = 6;

        // Шаги редактирования
        public const int EditingTitle = 7;
        public const int EditingText = 8;
        public const int EditingSchedule = 9;

        // Шаги удаления
        public const int DeletingReminder = 10;
    }
}
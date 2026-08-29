using System.Globalization;

namespace KidTime.Domain.Localization;

/// <summary>
/// Russian wording for the controlled PC. Two things drive most of the shape here: Russian has
/// three plural forms, and a noun after a preposition changes case - so sentences that embed a
/// duration or a deadline are built as label-and-value ("Осталось времени: 15 минут") instead of
/// being glued together from an English word order that would need a different case per slot.
/// </summary>
internal sealed class RussianAgentStrings : AgentStrings
{
    public override CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("ru-RU");
    public override AgentLanguage Language => AgentLanguage.Russian;

    /// <summary>0 = "минута", 1 = "минуты", 2 = "минут".</summary>
    private static int PluralForm(int count)
    {
        if (count % 100 is >= 11 and <= 14) return 2;
        return (count % 10) switch { 1 => 0, 2 or 3 or 4 => 1, _ => 2 };
    }

    private static string Pick(int count, string one, string few, string many) =>
        PluralForm(count) switch { 0 => one, 1 => few, _ => many };

    protected override string HoursOnly(int hours) => $"{hours} ч";
    protected override string HoursAndMinutes(int hours, int minutes) => $"{hours} ч {minutes:00} мин";
    protected override string MinutesOnly(int minutes) => $"{minutes} мин";
    protected override string MinuteWord(int count) => Pick(count, "минута", "минуты", "минут");
    protected override string SecondWord(int count) => Pick(count, "секунда", "секунды", "секунд");

    /// <summary>Accusative, for the "через ..." countdowns: "через 1 минуту", "через 5 минут".</summary>
    private string CountdownAfterThrough(int seconds)
    {
        if (seconds < 60) return $"{seconds} {Pick(seconds, "секунду", "секунды", "секунд")}";
        var minutes = (int)Math.Ceiling(seconds / 60d);
        return $"{minutes} {Pick(minutes, "минуту", "минуты", "минут")}";
    }

    protected override string TodayAt(string time) => $"сегодня в {time}";
    protected override string WeekdayAt(string weekday, string time) => $"{weekday} в {time}";
    public override string NotYet => "Ещё нет";
    public override string JustNow => "Только что";
    protected override string MinutesAgo(int minutes) => $"{minutes} мин назад";
    protected override string HoursAgo(int hours) => $"{hours} ч назад";

    public override string Allowed => "Разрешено";
    public override string PcScopeName => "ПК";
    public override string NoLimit => "без лимита";

    public override string DeviceBlockedByParent => "Этот компьютер заблокирован родителем.";
    public override string DeviceTemporarilyBlocked => "Этот компьютер временно заблокирован.";
    public override string DeviceDailyLimitReached => "Дневной лимит времени за компьютером исчерпан.";
    public override string DeviceOutsideSchedule => "Сейчас пользоваться компьютером нельзя.";

    public override string ApplicationBlockedByParent(string application) =>
        $"«{application}»: доступ заблокирован родителем.";
    public override string ApplicationDailyLimitReached(string application) =>
        $"«{application}»: дневной лимит времени исчерпан.";
    public override string ApplicationOutsideSchedule(string application) =>
        $"«{application}»: сейчас недоступно.";

    public override string ShortReasonManualBlock => "Родитель заблокировал";
    public override string ShortReasonDailyLimit => "Дневное время закончилось";
    public override string ShortReasonOutsideSchedule => "Сейчас неразрешённое время";
    public override string ShortReasonUnavailable => "Сейчас недоступно";

    public override string SignOutCountdownTitle(int seconds) =>
        $"Выход из системы через {CountdownAfterThrough(seconds)}";
    public override string ApplicationClosingTitle(string application, int seconds) =>
        $"«{application}» закроется через {CountdownAfterThrough(seconds)}";
    public override string SaveYourWorkNow(string shortReason) => $"{shortReason}. Сохраните работу сейчас.";

    public override string PcAvailableTitle => "Компьютер доступен";
    public override string PcAvailableMessage(string previousShortReason) =>
        $"Компьютером снова можно пользоваться. Причина до этого: {previousShortReason.ToLower(Culture)}.";

    public override string UpdatedTitle => "KidTime обновлён";
    public override string UpdatedMessage(string version) =>
        $"Установлена версия {version}. Для вас ничего не меняется.";

    public override string PcLimitChangedTitle => "Лимит времени за ПК изменён";
    public override string ApplicationLimitChangedTitle(string application) =>
        $"«{application}»: лимит времени изменён";
    protected override string LimitChangeDailyClause(string limitText) => $"дневное время теперь {limitText}";
    protected override string LimitChangeScheduleClause => "расписание изменилось";
    protected override string LimitChangeSentence(string scope, string clauses) => $"{scope}: {clauses}.";

    public override string PcTimeLeftTitle(int thresholdSeconds) =>
        $"Осталось времени за ПК: {Countdown(thresholdSeconds)}";
    public override string ApplicationTimeLeftTitle(string application, int thresholdSeconds) =>
        $"«{application}» — осталось времени: {Countdown(thresholdSeconds)}";
    public override string PcTimeLeftMessage => "Когда время закончится, Windows выполнит выход из системы.";
    public override string ApplicationTimeLeftMessage(string application) =>
        $"Когда время закончится, «{application}» закроется.";

    public override string ApplicationTimeTitle(string application) => $"«{application}»: время";
    public override string ApplicationRemainingUntil(string remaining, string deadline) =>
        $"Осталось времени: {remaining}. Доступно до: {deadline}.";
    public override string ApplicationRemainingToday(string remaining) =>
        $"Осталось сегодня: {remaining}.";
    public override string ApplicationAvailableUntil(string deadline) => $"Доступно до: {deadline}.";
    public override string ApplicationTimeLimited => "Время ограничено.";

    public override string ConnectingToServer => "Подключение к серверу";
    public override string DeviceNotEnrolled => "Компьютер не подключён";
    public override string Connected => "Подключено";
    public override string OfflineCachedRules => "Нет связи - действуют сохранённые правила";

    public override string RemovalEnterCredentials => "Введите почту и пароль родителя.";
    public override string RemovalAlreadyChecking => "KidTime уже проверяет запрос на удаление.";
    public override string RemovalAlreadyInProgress => "Удаление KidTime уже выполняется.";
    public override string RemovalCredentialsIncorrect => "Неверная почта или пароль родителя.";
    public override string RemovalAccepted => "Аккаунт родителя подтверждён. KidTime удаляется с этого компьютера.";
    public override string RemovalServerUnreachable =>
        "Не удалось проверить вход родителя на сервере. Проверьте подключение и попробуйте снова.";
    public override string RemovalWindowsFailed =>
        "Вход родителя принят, но Windows не смогла начать удаление KidTime. Попробуйте снова.";
    public override string RemovalServiceSilent => "Служба KidTime не ответила. Подождите немного и попробуйте снова.";
    public override string RemovalDialogFailed => "Что-то пошло не так. Попробуйте снова через минуту.";

    public override string TrayOpen => "Открыть KidTime";
    public override string TrayServerLabel => "Сервер";
    public override string TraySyncLabel => "Синхронизация";
    public override string TrayProfileLabel => "Контролируемый профиль";
    public override string TrayConnectingToService => "Подключение к службе";
    public override string TrayWaitingForService => "Ожидание службы";
    public override string TrayLoading => "Загрузка";
    public override string TraySyncFailed(string lastSuccess) => $"Ошибка (последний успех: {lastSuccess})";
    public override string TrayTooltipConnecting => "KidTime - подключение к службе";
    public override string TrayTooltipRemaining(string duration) => $"KidTime - осталось сегодня: {duration}";
    public override string TrayTooltipAvailable => "KidTime - время за компьютером доступно";
    public override string TrayTooltipUnavailable => "KidTime - время за компьютером недоступно";

    public override string HeadlineToday => "Твоё время сегодня";
    public override string HeadlineConnecting => "Подключение к службе KidTime";
    public override string HeadlineSignedInAs(string profile) => $"Выполнен вход: {profile}";
    public override string HeadlineLiveView => "Текущее время за компьютером, расписание и контролируемые приложения.";
    public override string BadgeConnecting => "Подключение";
    public override string BadgeAvailableNow => "Сейчас доступно";
    public override string BadgeUnavailable => "Недоступно";

    public override string TabToday => "Сегодня";
    public override string TabApps => "Приложения";
    public override string TabConnection => "Связь";
    public override string TabAbout => "О программе";

    public override string DailyScreenTime => "Время за компьютером за день";
    public override string WaitingForService => "Ожидание службы...";
    public override string LoadingTime => "загрузка времени";
    public override string LeftToday => "осталось сегодня";
    public override string Unlimited => "Без лимита";
    public override string NoDailyLimitCaption => "дневной лимит не задан";
    public override string UsedOf(string used, string total) => $"Использовано {used} из {total}";
    public override string UsedToday(string used) => $"Использовано сегодня: {used}";

    public override string WeeklyScheduleCaption => "РАСПИСАНИЕ НА НЕДЕЛЮ";
    public override string LoadingSchedule => "Загрузка расписания";
    public override string SchedulePlaceholder => "Здесь появится информация о расписании.";
    public override string ScreenTimeUnavailableTitle => "Время за компьютером недоступно";

    public override string NoAppLimitsTitle => "Нет лимитов и блокировок приложений";
    public override string NoAppLimitsDetail => "В этом списке появляются только приложения, которые ограничил родитель.";

    public override string ServerCaption => "СЕРВЕР";
    public override string SyncCaption => "СИНХРОНИЗАЦИЯ";
    public override string ProfileCaption => "КОНТРОЛИРУЕМЫЙ ПРОФИЛЬ";
    public override string Offline => "Нет связи";
    public override string NoContactYet => "Связи ещё не было";
    public override string ContactRelative(string relative) => $"Связь: {relative}";
    public override string CachedRulesActive => "Действуют сохранённые правила";
    public override string SyncNeedsAttention => "Синхронизация с ошибкой";
    public override string Waiting => "Ожидание";
    public override string NoSuccessfulSyncYet => "Успешной синхронизации ещё не было";
    public override string RulesSynchronized => "Правила и статистика синхронизированы";
    public override string NotSelected => "Не выбран";
    public override string RuleRevisionPlaceholder => "Версия правил --";
    public override string CachedRuleRevision(long revision) => $"Сохранённая версия правил: {revision}";
    public override string WaitingForLiveStatus => "Ожидание текущего состояния";
    public override string LiveStatusUpdated(string time) => $"Состояние обновлено: {time}";

    public override string VersionCaption => "Версия";
    public override string VersionWithNumber(string version) => $"Версия {version}";
    public override string UpdatesInBackground => "KidTime обновляется сам в фоновом режиме.";
    public override string WhatKidTimeSees => "Что KidTime видит";
    public override string WhatKidTimeSeesDetail =>
        "Активное время за этим компьютером и то, какое приложение открыто. И всё.";
    public override string WhatKidTimeNeverSees => "Чего KidTime не видит никогда";
    public override string WhatKidTimeNeverSeesDetail =>
        "Сайты, поисковые запросы, переписку, нажатия клавиш, экран, камеру и микрофон.";

    public override string RemoveCardTitle => "Удалить KidTime с этого компьютера";
    public override string RemoveCardDetail =>
        "Чтобы удалить службу Windows, локальные правила, статистику и файлы программы, родитель должен войти в свой аккаунт через интернет.";
    public override string RemoveButton => "Удалить KidTime";
    public override string RemoveDialogTitle => "Удалить KidTime с этого компьютера?";
    public override string RemoveDialogIntro =>
        "Службы KidTime и локальные данные будут удалены безвозвратно. Для проверки аккаунта родителя нужен доступ к серверу.";
    public override string ParentEmail => "Почта родителя";
    public override string ParentPassword => "Пароль родителя";
    public override string VerifyAndRemove => "Проверить и удалить";
    public override string Cancel => "Отмена";
    public override string CheckingParentAccount => "Проверка аккаунта родителя";
    public override string CheckingParentAccountDetail => "KidTime безопасно проверяет вход на сервере.";
    public override string RemovalStarted => "Удаление начато";
    public override string RemovalNotDone => "KidTime не удалён";

    public override string AppStatusBlocked => "Заблокировано";
    public override string AppStatusLimitReached => "Лимит исчерпан";
    public override string AppStatusOutsideSchedule => "Вне расписания";
    public override string AppStatusAvailable => "Доступно";
    public override string AppDailySummary(string remaining, string limit) =>
        $"Осталось {remaining} из {limit} в день";
    public override string AppNoDailyLimit => "Дневной лимит не задан";

    public override string ScheduleAlwaysAvailable => "Доступно в любое время";
    public override string ScheduleAvailableUntil(string deadline) => $"Доступно до: {deadline}";
    public override string ScheduleAvailableNow => "Доступно сейчас";
    public override string ScheduleAvailableAgain(string deadline) => $"Снова доступно: {deadline}";
    public override string ScheduleNoneThisWeek => "На этой неделе недоступно";
    public override string ScheduleNoRestriction => "Расписание не ограничивает время за компьютером.";
    public override string ScheduleRemainsInWindow(string duration) =>
        $"До конца текущего интервала расписания осталось {duration}.";
    public override string ScheduleNextWindowBegins(string deadline) =>
        $"Следующий разрешённый интервал начнётся: {deadline}.";
    public override string ScheduleCurrentAllows => "Текущее расписание разрешает пользоваться компьютером.";
    public override string ScheduleNoWindowFound => "На этой неделе разрешённых интервалов не найдено.";

    public override string CountdownCardTimeLeft => "Осталось";
    public override string CountdownCardDismiss => "Понятно";

    // ---------------------------------------------------------------- extra time

    public override string ExtraTimeCardTitle => "Нужно больше времени?";
    public override string ExtraTimeForScope(string scope) => $"Можно попросить у родителей ещё времени. Для чего: {scope}.";
    public override string ExtraTimeChooseHowMuch => "Выберите, сколько попросить.";
    public override string ExtraTimeAmount(int minutes) => $"+{minutes} мин";
    public override string ExtraTimeAskButton => "Попросить ещё времени";
    public override string ExtraTimeSent => "Запрос отправлен. Решают родители.";
    public override string ExtraTimeWaitingForParent => "Ждём ответа родителей.";
    public override string ExtraTimeGrantedCaption(int minutes) =>
        $"Родители добавили {minutes} {Pick(minutes, "минуту", "минуты", "минут")}.";
    public override string ExtraTimeDeniedCaption =>
        "Родители отказали. Попросить снова можно, когда начнётся следующее экранное время.";
    public override string ExtraTimeDeniedUntilNextPeriod =>
        "Родители уже отказали. Попросить снова можно, когда начнётся следующее экранное время.";

    public override string ExtraTimeNotRunningOutYet => "Времени пока достаточно.";
    public override string ExtraTimeAlreadyAsked => "Запрос уже отправлен. Ждём ответа.";
    public override string ExtraTimeTooManyToday => "Сегодня вы просили уже достаточно раз.";
    public override string ExtraTimeNotPossible => "Сейчас попросить больше времени нельзя.";

    public override string ExtraTimeApprovedTitle => "Время добавлено";
    public override string ExtraTimeApprovedMessage(string scope, int minutes) =>
        $"Родители добавили {minutes} {Pick(minutes, "минуту", "минуты", "минут")}. Для чего: {scope}.";
    public override string ExtraTimeDeniedTitle => "Без дополнительного времени";
    public override string ExtraTimeDeniedMessage(string scope) =>
        $"Родители отказали в дополнительном времени. Для чего: {scope}.";

    public override string ExtraTimeAddedToday(int minutes) => $"Добавлено сегодня: +{minutes} мин";
}

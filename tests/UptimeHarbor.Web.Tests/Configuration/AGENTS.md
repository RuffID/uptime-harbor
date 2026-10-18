# Тесты Web-конфигурации

Проверяются DI options, безопасные JSON-примеры, project metadata и разделение Serilog/app configuration.

Не запускать Web-host, backup/migrations или реальную конфигурацию с секретами. Статический configuration contract не доказывает runtime-загрузку внешнего файла.

Telegram options проверять на корректном вымышленном токене; отдельные случаи отклоняют заглушку, числовой ID и неверный формат без вывода значения.

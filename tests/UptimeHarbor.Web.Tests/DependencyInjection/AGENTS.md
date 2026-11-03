# Тесты composition root

CompleteRegistration повторяет полный набор production modules, включая AddClusterWeb; InvitationAdministration должен разрешаться в scope. Build графа не запускает host/hosted services и не проверяет сеть или БД.

Service collection тестируется без запуска приложения; проверяются lifetime, разрешение сервисов и инфраструктурные registrations.

Не открывать БД при resolve. При обновлении локальных DLL сохранять проверку adapter/repository graph и порядка startup migration до Identity bootstrap.

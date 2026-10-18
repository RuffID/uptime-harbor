# Результаты прикладных сценариев

`ServiceResult`, `ServiceResult<T>`, `ServiceError` и `ServiceErrorType` описывают успех и ожидаемые отказы; ошибка может указывать поле.

Не хранить HTTP status code и MVC-типы. Соответствие результата JSON envelope принадлежит `UptimeHarbor.Web/Http`; непредвиденные исключения не маскировать успешным результатом.

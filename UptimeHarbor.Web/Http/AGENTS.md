# Маппинг HTTP-результатов

`JsonResultMapper` преобразует Application `ServiceResult` в `{ success, data? }` либо `{ success: false, message, field? }`. `ModelStateErrorResponseFactory` согласует автоматические MVC ошибки с тем же envelope.

Expected error types определяют 400/401/403/404/409; неизвестный тип считается ошибкой контракта. Не выдавать прямой тест factory за проверку реального HTTP formatter pipeline.

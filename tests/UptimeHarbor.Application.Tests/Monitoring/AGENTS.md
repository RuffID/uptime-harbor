# Тесты monitoring use case

Fake endpoint checker и Unit of Work проверяют сохранение переходов, stale results и публикацию событий.

Событие проверять после успешного сохранения. Конкурентный конфликт, reset endpoint и disable не должны превращать устаревший результат в актуальное состояние.

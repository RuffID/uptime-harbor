# Подключение контейнера с ASP.NET Core

Инструкция относится к приложению, которое нужно проверять из UptimeHarbor. Здесь контейнер — отдельное приложение с HTTP endpoint. Доступ к Docker API, Docker socket и установка агента не нужны. Приложение может работать и без Docker: контракт проверки тот же.

## 1. Что должно возвращать приложение

На анонимный `GET /monitoring/health` приложение должно отвечать `200 OK`, `Content-Type: application/json` и JSON:

```json
{
  "name": "orders-api",
  "startedAtUtc": "2026-11-01T03:20:00Z"
}
```

- `name` — имя приложения, непустое после удаления пробелов по краям, до 256 символов. Оно отображается как полученное от endpoint имя контейнера.
- `startedAtUtc` — время запуска текущего процесса приложения в UTC. Принимаются окончания `Z` и `+00:00`. Время без часового пояса, с другим смещением или в будущем относительно проверяющего узла не принимается.
- Время запуска остаётся одинаковым между запросами и меняется после перезапуска процесса. Нельзя возвращать текущее время каждого запроса, время сборки образа или загрузки Ubuntu.

Проект принимает успешные HTTP-коды `2xx`, но для этого endpoint используйте `200`. Максимальный размер тела — 64 KiB. Авторизация и переходы `301/302/307/308` не поддерживаются: указывайте конечный HTTPS URL. Обычный ответ `Healthy` от стандартного health check не соответствует этому JSON-контракту.

Полный контракт и обработка ошибок описаны в [Monitoring.md](Monitoring.md#контракты-endpoint).

## 2. Добавить файлы в проверяемое ASP.NET Core приложение

Пример использует MVC controller в современном ASP.NET Core. Замените пространство имён `OrdersApi` на пространство имён своего приложения. Создайте каталог `Monitoring` рядом с `Program.cs` и следующие три файла.

### Monitoring/ApplicationStartInfo.cs

```csharp
namespace OrdersApi.Monitoring;

public record ApplicationStartInfo(string Name, DateTimeOffset StartedAtUtc);
```

### Monitoring/ServiceCollectionExtensions.cs

```csharp
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace OrdersApi.Monitoring;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUptimeHarborMonitoring(
        this IServiceCollection services)
    {
        using Process process = Process.GetCurrentProcess();

        DateTimeOffset startedAtUtc = new(process.StartTime.ToUniversalTime());
        ApplicationStartInfo startInfo = new("orders-api", startedAtUtc);
        services.AddSingleton(startInfo);
        services.AddControllers();

        return services;
    }
}
```

Замените `orders-api` на имя своего приложения. Время читается один раз при регистрации зависимостей из времени запуска процесса, а не вычисляется при каждом запросе. Перевод всей Ubuntu или контейнера в часовой пояс UTC не требуется. Часы проверяемого сервера и узлов UptimeHarbor должны быть синхронизированы.

### Monitoring/MonitoringHealthController.cs

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OrdersApi.Monitoring;

[ApiController]
[AllowAnonymous]
[Route("monitoring/health")]
public class MonitoringHealthController(ApplicationStartInfo startInfo) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "no-store";

        return Ok(new
        {
            name = startInfo.Name,
            startedAtUtc = startInfo.StartedAtUtc
        });
    }
}
```

При стандартной сериализации `DateTimeOffset` время будет иметь окончание `+00:00`, что подходит UptimeHarbor. Если приложение использует собственный JSON converter или меняет имена полей, проверьте фактический ответ: нужны именно `name` и `startedAtUtc`.

Этот пример подтверждает, что приложение принимает запросы. Если нужно учитывать готовность БД или других обязательных зависимостей, добавьте их проверку в этот endpoint: при отказе возвращайте `503 Service Unavailable`, при успехе — тот же JSON. Подробности ошибок, секреты и параметры подключений в публичный ответ не добавляйте.

## 3. Подключить endpoint в Program.cs

Добавьте пространство имён:

```csharp
using OrdersApi.Monitoring;
```

После создания `builder`, до `builder.Build()`, вызовите:

```csharp
builder.Services.AddUptimeHarborMonitoring();
```

После создания `app`, до `app.Run()`, подключите контроллеры, если они ещё не подключены:

```csharp
app.MapControllers();
```

Сохраните существующие регистрации и middleware приложения. Если `AddControllers()` уже вызывается в вашем модуле регистрации, второй вызов в примере расширения не нужен. Не создавайте второй маршрут с тем же адресом. `[AllowAnonymous]` разрешает проверку без входа; собственный middleware авторизации и nginx также должны пропускать этот путь без перенаправления на страницу входа.

## 4. Открыть доступ через существующий nginx

Если nginx уже проксирует весь домен приложения, новый маршрут обычно доступен сразу:

```text
https://api.example.com/monitoring/health
```

Для nginx, установленного на Ubuntu-хосте, ниже пример отдельного location. Предполагается, что приложение в контейнере слушает HTTP на `8080`, а на хосте этот порт опубликован только на `127.0.0.1:18080`. В существующий сервис Docker Compose можно добавить или адаптировать:

```yaml
environment:
  ASPNETCORE_HTTP_PORTS: "8080"
ports:
  - "127.0.0.1:18080:8080"
```

Не заменяйте этим фрагментом остальные настройки сервиса. Пример `ASPNETCORE_HTTP_PORTS` подходит для ASP.NET Core 8 и новее; явная конфигурация Kestrel должна соответствовать выбранному порту. Для nginx в другом контейнере используйте адрес приложения в общей Docker-сети вместо `127.0.0.1:18080`.

В уже работающий HTTPS `server`-блок домена добавьте:

```nginx
location = /monitoring/health {
    proxy_pass http://127.0.0.1:18080/monitoring/health;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_cache off;
}
```

Домен и выпущенный сертификат Let's Encrypt остаются в существующем конфиге. Если в этом блоке включён `auth_basic`, для публичного health location добавьте `auth_basic off;`. Другие ограничения доступа также должны разрешать анонимный GET. IP allowlist для endpoint не требуется.

Если приложение перенаправляет внутренний HTTP на HTTPS, настройте обработку forwarded headers от доверенного nginx до HTTPS redirection. Иначе ответом может стать redirect, который UptimeHarbor считает ошибкой. Используйте существующие настройки доверенного proxy вашего приложения.

Можно добавить location в уже созданный мониторинговый HTTPS-блок на `43871` из [инструкции Ubuntu](Ubuntu%20server%20monitoring.md). Тогда URL будет:

```text
https://health.example.com:43871/monitoring/health
```

Системный `/health` Ubuntu при этом остаётся отдельным маршрутом. Для нескольких приложений создайте разные пути, например `/orders-api/health` и `/billing-api/health`, с `proxy_pass` на соответствующий контейнер. Внешний порт берётся из URL; `43871` в коде проекта не закреплён. Если UFW активен и используется этот порт, правило приведено в инструкции Ubuntu. Для существующего доступного HTTPS на `443` отдельный порт открывать не нужно.

После изменения конфига проверьте и перечитайте его на сервере:

```bash
sudo nginx -t
sudo systemctl reload nginx
```

## 5. Проверить ответ перед добавлением

С машины, где работает UptimeHarbor, либо с каждого назначенного проверяющего узла выполните:

```bash
curl -i https://api.example.com/monitoring/health
```

Для варианта с отдельным портом:

```bash
curl -i https://health.example.com:43871/monitoring/health
```

Ожидаются `200`, JSON с двумя обязательными полями и доверенный HTTPS-сертификат на указанный домен. Не используйте `-L` или `-k`: они скрывают redirect и ошибки сертификата, на которых проверка проекта завершится неудачей. Повторный запрос должен вернуть то же время запуска; после перезапуска приложения оно должно измениться. Не включайте кеширование этого пути в CDN или reverse proxy.

Если домен балансирует запросы между репликами, такой URL проверяет выбранную балансировщиком реплику. Для отдельного наблюдения за каждым контейнером нужен отдельный URL, который всегда ведёт в конкретный экземпляр. Например, разные поддомены или пути nginx с отдельными upstream. Иначе имя и время запуска могут чередоваться между ответами.

## 6. Добавить приложение в UptimeHarbor

В обычном режиме (`Cluster.Enabled = false`):

1. Войдите как администратор и откройте **Панель мониторинга** (`/`).
2. Нажмите **Создать**, выберите тип **Приложение** и задайте имя.
3. При необходимости выберите **Сервер**. Приложение можно оставить без сервера и привязать позже в редакторе; endpoint проверяется независимо.
4. В **Адрес проверки** вставьте полный URL из предыдущего шага, включая порт и путь, если они нужны.
5. Добавьте описание, оставьте **Включить мониторинг** и нажмите **Создать**.
6. Дождитесь успешной проверки. На dashboard появятся полученное имя, время запуска и текущий uptime. До первой успешной проверки эти данные неизвестны.

В уже включённом кластерном режиме форма использует заявки: на панели мониторинга создайте заявку типа «Приложение», задайте имя, заполните endpoint и назначьте проверяющие узлы (**observers / NodeId**). Дождитесь принятия конфигурации; наличие Pending-заявки ещё не означает, что мониторинг начался. Каждый назначенный узел должен иметь доступ к тому же URL. Порядок включения и readiness описан отдельно в [Cluster operations.md](Cluster%20operations.md); ради добавления контейнера переключать режим не требуется.

Если endpoint недоступен, проверьте доступность приложения, адрес и порт upstream в nginx, сертификат, отсутствие авторизации/redirect, имена JSON-полей и UTC-время. Статус доступности меняется с учётом настроенного порога неудачных проверок, а uptime рассчитывается из последнего успешно полученного `startedAtUtc`.

## Границы проверки

Инструкция основана на текущем HTTP-контракте и административных формах UptimeHarbor. Пример нужно добавить, собрать и проверить в конкретном приложении. Запуск контейнера, nginx, реальные HTTP-запросы и проверка перезапуска при подготовке документа не выполнялись.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1", EnvironmentVariableTarget.Process);
Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1", EnvironmentVariableTarget.Process);
Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1", EnvironmentVariableTarget.Process);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("code-run", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

var dataDirectory = Path.Combine(app.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var accountsPath = Path.Combine(dataDirectory, "accounts.json");
var store = new SqliteStore(Path.Combine(dataDirectory, "sharpstart.db"));
store.ImportLegacyAccounts(accountsPath);
if (store.AccountCount == 0)
    store.AddAccount(CreateAccount("student@sharpstart.ru", "SharpStart2026!", "Ученик"));
app.Lifetime.ApplicationStopping.Register(store.Dispose);
var sessions = new ConcurrentDictionary<string, string>();
var accountLock = new SemaphoreSlim(1, 1);

app.MapPost("/api/auth/register", async (AuthRequest request) =>
{
    var email = (request.Email ?? "").Trim().ToLowerInvariant();
    var password = request.Password ?? "";
    var name = string.IsNullOrWhiteSpace(request.Name) ? "Ученик" : request.Name.Trim();
    if (email.Length < 5 || email.Length > 254 || !email.Contains('@') || email.Any(char.IsControl) || password.Length < 8 || name.Length > 60)
        return Results.BadRequest(new { message = "Укажите корректную почту и пароль не короче 8 символов." });
    await accountLock.WaitAsync();
    try
    {
        var account = CreateAccount(email, password, name);
        if (!store.AddAccount(account))
            return Results.Conflict(new { message = "Аккаунт с такой почтой уже существует." });
        var token = IssueSession(sessions, email);
        return Results.Ok(new { token, name = account.Name, email = account.Email });
    }
    finally { accountLock.Release(); }
});
app.MapPost("/api/auth/login", (AuthRequest request) =>
{
    var email = (request.Email ?? "").Trim().ToLowerInvariant();
    var account = store.FindAccount(email);
    if (account is null || !VerifyPassword(request.Password ?? "", account))
        return Results.Unauthorized();
    var token = IssueSession(sessions, email);
    return Results.Ok(new { token, name = account.Name, email = account.Email });
});
app.MapGet("/api/auth/me", (HttpRequest request) =>
{
    return TryGetSessionEmail(request, sessions, out var email) && store.FindAccount(email) is { } account
        ? Results.Ok(new { name = account.Name, email = account.Email })
        : Results.Unauthorized();
});
app.MapPost("/api/auth/logout", (HttpRequest request) =>
{
    var token = GetSessionToken(request);
    if (token is not null) sessions.TryRemove(token, out _);
    return Results.NoContent();
});

var lessons = LoadLessons(app.Environment.ContentRootPath);

app.MapGet("/api/progress", (HttpRequest request) =>
    TryGetSessionEmail(request, sessions, out var email)
        ? Results.Ok(BuildProgressResponse(store.GetProgress(email), lessons))
        : Results.Unauthorized());
app.MapPut("/api/progress/reading", (ReadProgressRequest request, HttpRequest httpRequest) =>
{
    if (!TryGetSessionEmail(httpRequest, sessions, out var email)) return Results.Unauthorized();
    if (request.Percent is < 0 or > 100) return Results.BadRequest(new { message = "Процент должен быть от 0 до 100." });
    if (lessons.All(lesson => lesson.Id != request.LessonId)) return Results.NotFound(new { message = "Урок не найден." });
    store.SaveReadingProgress(email, request.LessonId, request.Percent);
    return Results.NoContent();
});

app.MapGet("/api/lessons", (string? q, string? level) => lessons
    .Where(x => string.IsNullOrWhiteSpace(q) || $"{x.Track} {x.Title} {x.Description} {x.Level}".Contains(q, StringComparison.OrdinalIgnoreCase))
    .Where(x => string.IsNullOrWhiteSpace(level) || x.Level == level)
    .OrderBy(x => x.SortOrder)
    .Select(x => new { x.Id, x.Slug, x.SortOrder, x.Track, x.Title, x.Description, x.Level, x.Duration }));
app.MapGet("/api/lessons/{id:int}", (int id) => lessons.FirstOrDefault(x => x.Id == id) is { } lesson
    ? Results.Ok(PublicLesson(lesson)) : Results.NotFound(new { message = "Урок не найден" }));
app.MapGet("/api/lessons/{slug}", (string slug) => lessons.FirstOrDefault(x => x.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)) is { } lesson
    ? Results.Ok(PublicLesson(lesson)) : Results.NotFound(new { message = "Урок не найден" }));
app.MapPost("/api/lessons/{slug}/quiz", (string slug, QuizCheckRequest request) =>
{
    var lesson = lessons.FirstOrDefault(x => x.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    if (lesson is null) return Results.NotFound(new { message = "Урок не найден" });
    var check = lesson.Checks.FirstOrDefault(x => x.Id == request.CheckId);
    if (check is null) return Results.NotFound(new { message = "Вопрос не найден" });
    var expectedAnswer = string.IsNullOrWhiteSpace(check.Answer) ? lesson.Answer : check.Answer;
    var correct = string.Equals(request.Option?.Trim(), expectedAnswer, StringComparison.OrdinalIgnoreCase);
    return Results.Ok(new { correct, message = correct ? "Верно. Можно идти дальше." : "Пока неверно. Вернись к объяснению и попробуй ещё раз." });
});
app.MapGet("/api/lessons/{slug}/solution", (string slug, string exerciseId) =>
{
    var lesson = lessons.FirstOrDefault(x => x.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    var exercise = lesson?.Exercises.FirstOrDefault(x => x.Id == exerciseId);
    return exercise is null ? Results.NotFound(new { message = "Решение не найдено" })
        : Results.Ok(new { solution = exercise.Solution, explanation = exercise.SolutionExplanation });
});
app.MapPost("/api/lessons/{slug}/check", async (string slug, ExerciseCheckRequest request, HttpContext context) =>
{
    if (context.Connection.RemoteIpAddress is not { } remoteAddress || !IPAddress.IsLoopback(remoteAddress))
        return Results.Json(new { success = false, error = "Проверка доступна только при локальной работе сайта." }, statusCode: StatusCodes.Status403Forbidden);
    var lesson = lessons.FirstOrDefault(x => x.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    var exercise = lesson?.Exercises.FirstOrDefault(x => x.Id == request.ExerciseId);
    if (exercise is null) return Results.NotFound(new { success = false, error = "Задание не найдено." });
    var code = request.Code ?? "";
    if (string.IsNullOrWhiteSpace(code) || code.Length > 12_000)
        return Results.BadRequest(new { success = false, error = "Код пустой или слишком большой (максимум 12 000 символов)." });
    if (exercise.Tests.Length == 0)
        return Results.Conflict(new { success = false, error = "Автопроверка для этого задания пока не настроена." });

    var results = new List<object>();
    var allPassed = true;
    var elapsed = 0d;
    foreach (var (test, index) in exercise.Tests.Take(3).Select((test, index) => (test, index)))
    {
        var run = await WindowsSandboxRunner.RunAsync(code, test.Input ?? "", context.RequestAborted);
        elapsed += run.ElapsedMilliseconds;
        var actual = NormalizeOutput(run.Success ? run.Output : run.Stderr);
        var expected = NormalizeOutput(test.ExpectedOutput);
        var passed = run.Success && actual == expected;
        allPassed &= passed;
        results.Add(new
        {
            test = index + 1,
            passed,
            message = passed ? "Тест пройден." : run.Success ? "Результат на этих входных данных отличается." : "Проверь ошибку компиляции.",
            actualOutput = passed ? null : actual,
            diagnostic = run.Success ? null : run.Stderr
        });
    }
    var lessonCompleted = allPassed && TryGetSessionEmail(context.Request, sessions, out var email)
        && store.MarkExercisePassed(email, lesson!.Id, exercise.Id, lesson);
    return Results.Ok(new
    {
        success = allPassed,
        lessonCompleted,
        message = allPassed ? "Все проверки пройдены." : "Есть проверки, которые пока не пройдены.",
        elapsedMilliseconds = Math.Round(elapsed, 1),
        results
    });
}).RequireRateLimiting("code-run");
app.MapGet("/api/course/sections", () => lessons
    .GroupBy(x => x.Level)
    .OrderBy(group => group.Min(x => x.SortOrder))
    .Select(group => new { name = group.Key, count = group.Count(), lessons = group.OrderBy(x => x.SortOrder).Select(x => new { x.Id, x.Slug, x.Track, x.Title }) }));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "SharpStart API" }));
app.MapPost("/api/code/run", async (RunCodeRequest request, HttpContext context) =>
{
    if (context.Connection.RemoteIpAddress is not { } remoteAddress || !IPAddress.IsLoopback(remoteAddress))
        return Results.Json(new { success = false, error = "Запуск кода доступен только при локальной работе сайта." }, statusCode: StatusCodes.Status403Forbidden);
    var code = request.Code ?? "";
    var input = request.Input ?? "";
    if (string.IsNullOrWhiteSpace(code) || code.Length > 12_000)
        return Results.BadRequest(new { success = false, error = "Код пустой или слишком большой (максимум 12 000 символов)." });
    if (input.Length > 2_000)
        return Results.BadRequest(new { success = false, error = "Ввод для Console.ReadLine не должен превышать 2 000 символов." });

    try
    {
        var result = await WindowsSandboxRunner.RunAsync(code, input, context.RequestAborted);
        var output = string.IsNullOrWhiteSpace(result.Output) && result.Success
            ? "Программа завершилась без вывода."
            : result.Output;
        return Results.Ok(new
        {
            result.Success,
            result.TimedOut,
            output,
            result.Stdout,
            result.Stderr,
            result.ElapsedMilliseconds,
            result.Error
        });
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new { success = false, error = error.Message });
    }
    catch (PlatformNotSupportedException error)
    {
        app.Logger.LogError(error, "Windows AppContainer is required to execute untrusted code.");
        return Results.Problem("Для безопасного запуска кода нужен Windows 10/11 с поддержкой AppContainer.",
            statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (Exception error)
    {
        app.Logger.LogError(error, "The C# sandbox failed to start; user code was not run outside the sandbox.");
        return Results.Problem("Не удалось безопасно создать песочницу. Код не был запущен.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("code-run");
app.MapFallbackToFile("index.html");
app.Run();

static Account CreateAccount(string email, string password, string name)
{
    var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    return new Account(email, name, salt, Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 120_000, HashAlgorithmName.SHA256, 32)));
}

static string? GetSessionToken(HttpRequest request)
{
    var authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization[7..].Trim()
        : null;
}

static bool TryGetSessionEmail(HttpRequest request, ConcurrentDictionary<string, string> sessions, out string email)
{
    var token = GetSessionToken(request);
    if (token is not null && sessions.TryGetValue(token, out var foundEmail))
    {
        email = foundEmail;
        return true;
    }
    email = "";
    return false;
}

static object BuildProgressResponse(AccountProgress progress, Lesson[] lessons)
{
    var completed = progress.CompletedLessons.ToHashSet();
    var studyDates = progress.StudyDays
        .Select(day => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture))
        .ToHashSet();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var firstDay = studyDates.Contains(today) ? today
        : studyDates.Contains(today.AddDays(-1)) ? today.AddDays(-1)
        : (DateOnly?)null;
    var currentStreak = 0;
    for (var day = firstDay; day is not null && studyDates.Contains(day.Value); day = day.Value.AddDays(-1))
        currentStreak++;

    var bestStreak = 0;
    var streak = 0;
    DateOnly? previous = null;
    foreach (var day in studyDates.OrderBy(value => value))
    {
        streak = previous is not null && day == previous.Value.AddDays(1) ? streak + 1 : 1;
        bestStreak = Math.Max(bestStreak, streak);
        previous = day;
    }

    var experience = progress.PassedExercises.Length * 10 + completed.Count * 25 + studyDates.Count * 5;
    var foundationIds = lessons.Where(lesson => lesson.Level == "Основы").Select(lesson => lesson.Id).ToArray();
    var achievements = new[]
    {
        new Achievement("first-code", "Первый код", "Успешно выполни первое задание.", progress.PassedExercises.Length >= 1),
        new Achievement("first-lesson", "Первый урок", "Заверши все задания одного урока.", completed.Count >= 1),
        new Achievement("steady-three", "Ритм обучения", "Занимайся три дня подряд.", currentStreak >= 3),
        new Achievement("first-hundred", "100 XP", "Набери первые сто очков опыта.", experience >= 100),
        new Achievement("quarter-course", "Четверть пути", "Заверши четверть курса.", lessons.Length > 0 && completed.Count * 4 >= lessons.Length),
        new Achievement("fundamentals", "Основы заложены", "Заверши все уроки раздела «Основы».", foundationIds.Length > 0 && foundationIds.All(completed.Contains))
    };
    return new
    {
        progress.CompletedLessons,
        progress.PassedExercises,
        progress.ReadingProgress,
        progress.StudyDays,
        gamification = new GamificationInfo(experience, currentStreak, bestStreak, achievements)
    };
}

static bool VerifyPassword(string password, Account account)
{
    var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(account.Salt), 120_000, HashAlgorithmName.SHA256, 32);
    return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(account.PasswordHash));
}

static string IssueSession(ConcurrentDictionary<string, string> sessions, string email)
{
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    sessions[token] = email;
    return token;
}

static Lesson[] LoadLessons(string contentRoot)
{
    var path = Path.Combine(contentRoot, "Content", "lessons.json");
    if (!File.Exists(path)) throw new FileNotFoundException("Файл уроков не найден.", path);
    var items = JsonSerializer.Deserialize<Lesson[]>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    if (items.Length == 0 || items.Select(x => x.Id).Distinct().Count() != items.Length ||
        items.Select(x => x.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
        throw new InvalidDataException("Каталог уроков пуст или содержит повторяющиеся идентификаторы.");
    return items.OrderBy(x => x.SortOrder).ToArray();
}

static object PublicLesson(Lesson lesson) => new
{
    lesson.Id, lesson.Slug, lesson.SortOrder, lesson.Track, lesson.Title, lesson.Description, lesson.Level, lesson.Duration,
    lesson.Chapter, lesson.Content, lesson.Code, lesson.Question, lesson.Options, lesson.Practice, lesson.StarterCode,
    lesson.Goals, lesson.Examples, lesson.CommonMistakes, lesson.Internals, lesson.Takeaways, lesson.SectionProject,
    checks = lesson.Checks.Select(check => new { check.Id, check.Question, check.Options }),
    exercises = lesson.Exercises.Select(exercise => new
    {
        exercise.Id, exercise.Title, exercise.Difficulty, exercise.Prompt, exercise.StarterCode, exercise.Hints,
        hasTests = exercise.Tests.Length > 0
    })
};

static string NormalizeOutput(string? value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

sealed class Lesson
{
    public int Id { get; init; }
    public string Slug { get; init; } = "";
    public int SortOrder { get; init; }
    public string Track { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Level { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Chapter { get; init; } = "";
    public string Content { get; init; } = "";
    public string Code { get; init; } = "";
    public string Question { get; init; } = "";
    public string Answer { get; init; } = "";
    public string[] Options { get; init; } = [];
    public string Practice { get; init; } = "";
    public string StarterCode { get; init; } = "";
    public string[] Goals { get; init; } = [];
    public LessonExample[] Examples { get; init; } = [];
    public CommonMistake[] CommonMistakes { get; init; } = [];
    public string Internals { get; init; } = "";
    public LessonCheck[] Checks { get; init; } = [];
    public LessonExercise[] Exercises { get; init; } = [];
    public string[] Takeaways { get; init; } = [];
    public SectionProject? SectionProject { get; init; }
}
record LessonExample(string Title, string Explanation, string Code, string Output);
record CommonMistake(string Mistake, string Fix);
record LessonCheck(string Id, string Question, string[] Options, string Answer = "");
record LessonExercise(string Id, string Title, string Difficulty, string Prompt, string StarterCode, string[] Hints, string Solution, string SolutionExplanation, TestCase[] Tests);
record TestCase(string? Input, string ExpectedOutput);
record SectionProject(string Title, string Description);
record QuizCheckRequest(string? CheckId, string? Option);
record ExerciseCheckRequest(string? ExerciseId, string? Code);
record AuthRequest(string? Email, string? Password, string? Name);
record ReadProgressRequest(int LessonId, int Percent);
record Achievement(string Id, string Title, string Description, bool Unlocked);
record GamificationInfo(int ExperiencePoints, int CurrentStreak, int BestStreak, Achievement[] Achievements);
record Account(string Email, string Name, string Salt, string PasswordHash);
record RunCodeRequest(string? Code, string? Input);



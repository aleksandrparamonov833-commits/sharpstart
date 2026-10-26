using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

sealed class SqliteStore : IDisposable
{
    private static readonly ExecCallback RowCallback = CollectRow;
    private readonly object gate = new();
    private IntPtr database;

    public SqliteStore(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var result = sqlite3_open(Path.GetFullPath(filePath), out database);
        if (result != 0)
        {
            var message = database == IntPtr.Zero ? "Не удалось открыть SQLite." : ErrorMessage();
            if (database != IntPtr.Zero) sqlite3_close(database);
            database = IntPtr.Zero;
            throw new InvalidOperationException(message);
        }

        Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS Accounts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Email TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Name TEXT NOT NULL,
                Salt TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE TABLE IF NOT EXISTS LessonProgress (
                AccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                LessonId INTEGER NOT NULL,
                Completed INTEGER NOT NULL DEFAULT 0,
                ReadPercent INTEGER NOT NULL DEFAULT 0 CHECK (ReadPercent BETWEEN 0 AND 100),
                UpdatedUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                PRIMARY KEY (AccountId, LessonId)
            );
            CREATE TABLE IF NOT EXISTS PassedExercises (
                AccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                LessonId INTEGER NOT NULL,
                ExerciseId TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                PRIMARY KEY (AccountId, LessonId, ExerciseId)
            );
            CREATE TABLE IF NOT EXISTS StudyDays (
                AccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                StudyDate TEXT NOT NULL,
                PRIMARY KEY (AccountId, StudyDate)
            );
            """);
    }

    public int AccountCount
    {
        get
        {
            lock (gate)
                return int.Parse(Query("SELECT COUNT(*) FROM Accounts;")[0][0], CultureInfo.InvariantCulture);
        }
    }

    public Account? FindAccount(string email)
    {
        lock (gate)
        {
            var rows = Query($"SELECT Email, Name, Salt, PasswordHash FROM Accounts WHERE Email = {Quote(email)} LIMIT 1;");
            return rows.Count == 0 ? null : new Account(rows[0][0], rows[0][1], rows[0][2], rows[0][3]);
        }
    }

    public bool AddAccount(Account account)
    {
        lock (gate)
        {
            try
            {
                Execute($"INSERT INTO Accounts (Email, Name, Salt, PasswordHash) VALUES ({Quote(account.Email)}, {Quote(account.Name)}, {Quote(account.Salt)}, {Quote(account.PasswordHash)});");
                return true;
            }
            catch (SqliteStoreException exception) when ((exception.ResultCode & 0xff) == 19)
            {
                return false;
            }
        }
    }

    public void ImportLegacyAccounts(string filePath)
    {
        if (!File.Exists(filePath)) return;
        var legacy = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(filePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var account in legacy)
            AddAccount(account);
    }

    public AccountProgress GetProgress(string email)
    {
        lock (gate)
        {
            var accountId = FindAccountId(email);
            if (accountId is null) return AccountProgress.Empty;

            var completed = Query($"SELECT LessonId FROM LessonProgress WHERE AccountId = {accountId.Value} AND Completed = 1 ORDER BY LessonId;")
                .Select(row => int.Parse(row[0], CultureInfo.InvariantCulture)).ToArray();
            var passed = Query($"SELECT LessonId, ExerciseId FROM PassedExercises WHERE AccountId = {accountId.Value} ORDER BY LessonId, ExerciseId;")
                .Select(row => $"{row[0]}:{row[1]}").ToArray();
            var reading = Query($"SELECT LessonId, ReadPercent FROM LessonProgress WHERE AccountId = {accountId.Value} AND ReadPercent > 0 ORDER BY LessonId;")
                .ToDictionary(row => int.Parse(row[0], CultureInfo.InvariantCulture), row => int.Parse(row[1], CultureInfo.InvariantCulture));
            var studyDays = Query($"SELECT StudyDate FROM StudyDays WHERE AccountId = {accountId.Value} ORDER BY StudyDate;")
                .Select(row => row[0]).ToArray();
            return new AccountProgress(completed, passed, reading, studyDays);
        }
    }

    public void SaveReadingProgress(string email, int lessonId, int percent)
    {
        lock (gate)
        {
            var accountId = FindAccountId(email);
            if (accountId is null) return;
            RecordStudyDay(accountId.Value);
            Execute($"""
                INSERT INTO LessonProgress (AccountId, LessonId, ReadPercent)
                VALUES ({accountId.Value}, {lessonId}, {percent})
                ON CONFLICT (AccountId, LessonId) DO UPDATE SET
                    ReadPercent = MAX(LessonProgress.ReadPercent, excluded.ReadPercent),
                    UpdatedUtc = strftime('%Y-%m-%dT%H:%M:%fZ','now');
                """);
        }
    }

    public bool MarkExercisePassed(string email, int lessonId, string exerciseId, Lesson lesson)
    {
        lock (gate)
        {
            var accountId = FindAccountId(email);
            if (accountId is null) return false;
            RecordStudyDay(accountId.Value);
            Execute($"INSERT OR IGNORE INTO PassedExercises (AccountId, LessonId, ExerciseId) VALUES ({accountId.Value}, {lessonId}, {Quote(exerciseId)});");
            var required = lesson.Exercises.Where(exercise => exercise.Tests.Length > 0).Select(exercise => exercise.Id).ToHashSet(StringComparer.Ordinal);
            if (required.Count == 0) return false;
            var passed = Query($"SELECT ExerciseId FROM PassedExercises WHERE AccountId = {accountId.Value} AND LessonId = {lessonId};")
                .Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
            if (!required.IsSubsetOf(passed)) return false;
            Execute($"""
                INSERT INTO LessonProgress (AccountId, LessonId, Completed)
                VALUES ({accountId.Value}, {lessonId}, 1)
                ON CONFLICT (AccountId, LessonId) DO UPDATE SET
                    Completed = 1,
                    UpdatedUtc = strftime('%Y-%m-%dT%H:%M:%fZ','now');
                """);
            return true;
        }
    }

    private long? FindAccountId(string email)
    {
        var rows = Query($"SELECT Id FROM Accounts WHERE Email = {Quote(email)} LIMIT 1;");
        return rows.Count == 0 ? null : long.Parse(rows[0][0], CultureInfo.InvariantCulture);
    }

    private void RecordStudyDay(long accountId)
    {
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Execute($"INSERT OR IGNORE INTO StudyDays (AccountId, StudyDate) VALUES ({accountId}, {Quote(day)});");
    }

    private void Execute(string sql)
    {
        lock (gate)
        {
            var result = sqlite3_exec(database, sql, null, IntPtr.Zero, out var errorPointer);
            if (result == 0) return;
            var message = errorPointer == IntPtr.Zero ? ErrorMessage() : Marshal.PtrToStringUTF8(errorPointer) ?? ErrorMessage();
            if (errorPointer != IntPtr.Zero) sqlite3_free(errorPointer);
            throw new SqliteStoreException(result, message);
        }
    }

    private List<string[]> Query(string sql)
    {
        lock (gate)
        {
            var rows = new List<string[]>();
            var handle = GCHandle.Alloc(rows);
            try
            {
                var result = sqlite3_exec(database, sql, RowCallback, GCHandle.ToIntPtr(handle), out var errorPointer);
                if (result == 0) return rows;
                var message = errorPointer == IntPtr.Zero ? ErrorMessage() : Marshal.PtrToStringUTF8(errorPointer) ?? ErrorMessage();
                if (errorPointer != IntPtr.Zero) sqlite3_free(errorPointer);
                throw new SqliteStoreException(result, message);
            }
            finally
            {
                handle.Free();
            }
        }
    }

    private string ErrorMessage() => Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) ?? "Ошибка SQLite.";

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static int CollectRow(IntPtr context, int columnCount, IntPtr values, IntPtr columnNames)
    {
        var rows = (List<string[]>)GCHandle.FromIntPtr(context).Target!;
        var row = new string[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            var value = Marshal.ReadIntPtr(values, index * IntPtr.Size);
            row[index] = value == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(value) ?? "";
        }
        rows.Add(row);
        return 0;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (database == IntPtr.Zero) return;
            sqlite3_close(database);
            database = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExecCallback(IntPtr context, int columnCount, IntPtr values, IntPtr columnNames);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, ExecCallback? callback, IntPtr context, out IntPtr errorMessage);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr pointer);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);
}

sealed class SqliteStoreException(int resultCode, string message) : Exception(message)
{
    public int ResultCode { get; } = resultCode;
}

sealed record AccountProgress(int[] CompletedLessons, string[] PassedExercises, Dictionary<int, int> ReadingProgress, string[] StudyDays)
{
    public static AccountProgress Empty { get; } = new([], [], new Dictionary<int, int>(), []);
}

using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SnapLog.Diagnostics;

namespace SnapLog.Storage;

/// <summary>
/// SQLite 存储。选它的原因是记录查看器需要"按时间/进程/关键词筛选 + 分页"，
/// 这正好是 CSV 的短板：CSV 每次筛选都得全文件扫描，而这里走索引。
///
/// 低开销的具体做法：
/// - 单条长连接 + 信号量串行，不反复开连接；
/// - 开启 WAL：写入不再阻塞读取，且掉电/强杀不会像 CSV 那样留下半行；
/// - INSERT 语句只预编译一次，循环复用；
/// - 列表查询只取 substr(text,1,160) 做摘要，不把大段文字拉进内存。
///
/// 注意 Microsoft.Data.Sqlite 的 *Async 方法内部仍是同步执行（SQLite 没有异步 I/O），
/// 所以这里统一把数据库操作放到线程池上跑，避免阻塞 UI。
/// </summary>
public sealed class SqliteActivityStore : IActivityRepository
{
    /// <summary>
    /// 存进库/从库里读的时间格式。字典序即时间序，便于直接用 SQL 比较。
    /// 注意精度只到秒：写入时会丢掉毫秒，读回来的时间戳毫秒恒为 0。
    /// 对"某时刻屏幕上有什么"这个用途，秒级足够，而且 CSV 和 SQL 里都更好读。
    /// </summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private const string SelectColumns =
        "id, timestamp, process, title, window_class, text, ocr_ms, capture_method, image_width, image_height, image_path, status, error";

    private const string ListColumns =
        "id, timestamp, process, title, length(text), ocr_ms, capture_method, status, substr(text, 1, 160)";

    private readonly string _databasePath;
    private readonly FileLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteConnection? _connection;
    private SqliteCommand? _insertCommand;
    private bool _disposed;

    public SqliteActivityStore(string databasePath, FileLogger log)
    {
        _databasePath = databasePath;
        _log = log;
    }

    public string Location => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunAsync<bool>(connection =>
        {
            // 分开执行：journal_mode 会返回一行结果，混在多语句里容易踩到驱动的边界行为。
            ExecuteScalarText(connection, "PRAGMA journal_mode = WAL;");
            ExecuteScalarText(connection, "PRAGMA synchronous = NORMAL;");
            ExecuteScalarText(connection, "PRAGMA busy_timeout = 5000;");
            ExecuteScalarText(connection, "PRAGMA temp_store = MEMORY;");
            ExecuteScalarText(connection, "PRAGMA cache_size = -4000;");

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS activity (
                        id             INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp      TEXT    NOT NULL,
                        process        TEXT    NOT NULL DEFAULT '',
                        title          TEXT    NOT NULL DEFAULT '',
                        text           TEXT    NOT NULL DEFAULT '',
                        ocr_ms         INTEGER NOT NULL DEFAULT 0,
                        capture_method TEXT    NOT NULL DEFAULT '',
                        image_width    INTEGER NOT NULL DEFAULT 0,
                        image_height   INTEGER NOT NULL DEFAULT 0,
                        image_path     TEXT    NOT NULL DEFAULT '',
                        status         TEXT    NOT NULL DEFAULT 'Ok',
                        error          TEXT    NOT NULL DEFAULT '',
                        updated_at     TEXT    NOT NULL DEFAULT ''
                    );

                    CREATE INDEX IF NOT EXISTS idx_activity_timestamp ON activity(timestamp DESC);
                    CREATE INDEX IF NOT EXISTS idx_activity_process   ON activity(process);
                    CREATE INDEX IF NOT EXISTS idx_activity_status    ON activity(status);

                    CREATE TABLE IF NOT EXISTS summary_runs (
                        id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        started_at    TEXT    NOT NULL,
                        finished_at   TEXT    NOT NULL,
                        trigger       TEXT    NOT NULL DEFAULT '',
                        success       INTEGER NOT NULL DEFAULT 0,
                        provider      TEXT    NOT NULL DEFAULT '',
                        attempts      INTEGER NOT NULL DEFAULT 0,
                        markdown      TEXT    NOT NULL DEFAULT '',
                        saved_path    TEXT    NOT NULL DEFAULT '',
                        message       TEXT    NOT NULL DEFAULT '',
                        record_count  INTEGER NOT NULL DEFAULT 0,
                        image_count   INTEGER NOT NULL DEFAULT 0,
                        elapsed_ms    INTEGER NOT NULL DEFAULT 0,
                        pushed_at     TEXT    NOT NULL DEFAULT '',
                        covered_day   TEXT    NOT NULL DEFAULT '',
                        covered_marks INTEGER NOT NULL DEFAULT 0,
                        covered_text_rev TEXT NOT NULL DEFAULT ''
                    );

                    CREATE INDEX IF NOT EXISTS idx_summary_runs_started ON summary_runs(started_at DESC);
                    """;
                schema.ExecuteNonQuery();
            }

            // 老版本的库没有这两列。CREATE TABLE IF NOT EXISTS 不会补列，必须显式迁移。
            EnsureColumn(connection, "activity", "window_class", "TEXT NOT NULL DEFAULT ''");
            // 记录最后一次被改动的时间（目前只有"识别结果回填"会改），
            // 用来判断某天的文字是不是在总结生成之后才补上的。
            EnsureColumn(connection, "activity", "updated_at", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "summary_runs", "pushed_at", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "summary_runs", "covered_day", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "summary_runs", "covered_marks", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "summary_runs", "covered_text_rev", "TEXT NOT NULL DEFAULT ''");

            // 早期版本把"已生成并保存到 …"这段说明错当成模型名写进了 provider 列，
            // 界面上那列会显示成一句文件路径。这种值没法还原成模型名，清成空更诚实（界面显示"—"）。
            using (var cleanup = connection.CreateCommand())
            {
                cleanup.CommandText =
                    "UPDATE summary_runs SET provider = '' " +
                    "WHERE provider LIKE '已生成并保存到%' OR provider LIKE '生成失败%'";
                cleanup.ExecuteNonQuery();
            }

            _insertCommand = connection.CreateCommand();
            _insertCommand.CommandText =
                """
                INSERT INTO activity
                    (timestamp, process, title, window_class, text, ocr_ms, capture_method,
                     image_width, image_height, image_path, status, error, updated_at)
                VALUES
                    ($timestamp, $process, $title, $windowClass, $text, $ocrMs, $captureMethod,
                     $imageWidth, $imageHeight, $imagePath, $status, $error, $updatedAt)
                """;
            foreach (var name in new[]
                     {
                         "$timestamp", "$process", "$title", "$windowClass", "$text", "$ocrMs",
                         "$captureMethod", "$imageWidth", "$imageHeight", "$imagePath", "$status", "$error",
                         "$updatedAt",
                     })
            {
                _insertCommand.Parameters.Add(new SqliteParameter(name, DBNull.Value));
            }

            _insertCommand.Prepare();

            return true;
        }, cancellationToken).ConfigureAwait(false);

        _log.Info($"SQLite 已就绪：{_databasePath}");
    }

    public async Task AppendAsync(ActivityRecord record, CancellationToken cancellationToken)
    {
        await RunAsync(connection =>
        {
            BindInsert(_insertCommand!, record);
            _insertCommand!.ExecuteNonQuery();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<PagedResult<ActivityListItem>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, 5000);
        var offset = Math.Max(0, query.Offset);

        return RunAsync(connection =>
        {
            var (whereSql, parameters) = BuildWhere(query);

            int total;
            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM activity{whereSql}";
                AddParameters(countCommand, parameters);
                total = Convert.ToInt32(countCommand.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            }

            var items = new List<ActivityListItem>();

            if (total > 0 && offset < total)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {ListColumns} FROM activity{whereSql} ORDER BY timestamp DESC, id DESC LIMIT $limit OFFSET $offset";
                AddParameters(command, parameters);
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$offset", offset);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(new ActivityListItem(
                        reader.GetInt64(0),
                        ParseTimestamp(reader.GetString(1)),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetString(6),
                        ParseStatus(reader.GetString(7)),
                        Flatten(reader.GetString(8))));
                }
            }

            return new PagedResult<ActivityListItem>(items, total, offset, limit);
        }, cancellationToken);
    }

    public Task<ActivityRecord?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM activity WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ActivityRecord>> GetRecentAsync(int count, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<ActivityRecord>>(connection =>
        {
            var limit = Math.Clamp(count, 1, 100_000);

            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {SelectColumns} FROM activity ORDER BY timestamp DESC, id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);

            var records = new List<ActivityRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    records.Add(ReadRecord(reader));
                }
            }

            // SQL 取的是最新 N 条（降序），调用方按时间顺序消费，这里翻回来。
            records.Reverse();
            return records;
        }, cancellationToken);

    public Task<IReadOnlyList<ActivityRecord>> GetByDayAsync(
        DateTime day,
        int limit,
        CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<ActivityRecord>>(connection =>
        {
            var from = day.Date;
            var to = from.AddDays(1).AddSeconds(-1);

            using var command = connection.CreateCommand();
            // 取当天"最近"的 limit 条再翻回正序：一天记录很多时，超预算截断保留的应该是靠后的部分。
            command.CommandText =
                $"SELECT {SelectColumns} FROM activity " +
                "WHERE timestamp >= $from AND timestamp <= $to " +
                "ORDER BY timestamp DESC, id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$from", from.ToString(TimeFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", to.ToString(TimeFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

            var records = new List<ActivityRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    records.Add(ReadRecord(reader));
                }
            }

            records.Reverse();
            return records;
        }, cancellationToken);

    public Task<IReadOnlyList<DayMark>> GetDayMarksAsync(
        DateTime fromDay,
        CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<DayMark>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT date(timestamp) AS day, COUNT(*), MAX(id),
                       MAX(CASE WHEN updated_at = '' THEN timestamp ELSE updated_at END)
                FROM activity
                WHERE timestamp >= $from
                GROUP BY day
                ORDER BY day ASC
                """;
            command.Parameters.AddWithValue("$from", fromDay.Date.ToString(TimeFormat, CultureInfo.InvariantCulture));

            var marks = new List<DayMark>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    marks.Add(new DayMark(day, reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3)));
                }
            }

            return marks;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> DeleteByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        return RunAsync<IReadOnlyList<string>>(connection =>
        {
            // 先把这些记录引用的截图路径读出来，再删行——删完就查不到了。
            var placeholders = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                placeholders[i] = $"$id{i}";
            }

            var inClause = string.Join(", ", placeholders);

            var paths = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT image_path FROM activity WHERE id IN ({inClause})";
                for (var i = 0; i < ids.Count; i++)
                {
                    select.Parameters.AddWithValue(placeholders[i], ids[i]);
                }

                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = $"DELETE FROM activity WHERE id IN ({inClause})";
                for (var i = 0; i < ids.Count; i++)
                {
                    delete.Parameters.AddWithValue(placeholders[i], ids[i]);
                }

                delete.ExecuteNonQuery();
            }

            return paths;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetProcessNamesAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT process FROM activity WHERE process <> '' GROUP BY process ORDER BY COUNT(*) DESC, process";

            var names = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }, cancellationToken);

    public Task<int> ImportAsync(IEnumerable<ActivityRecord> records, int batchSize, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            var size = Math.Clamp(batchSize, 50, 5000);
            var imported = 0;
            SqliteTransaction? transaction = null;

            try
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    transaction ??= connection.BeginTransaction();
                    _insertCommand!.Transaction = transaction;
                    BindInsert(_insertCommand, record);
                    _insertCommand.ExecuteNonQuery();
                    imported++;

                    // 分块提交：几十万行一次性提交会撑大 WAL 且中途失败全部回滚。
                    if (imported % size == 0)
                    {
                        transaction.Commit();
                        transaction.Dispose();
                        transaction = null;
                    }
                }

                if (transaction is not null)
                {
                    transaction.Commit();
                }
            }
            finally
            {
                transaction?.Dispose();

                // 必须清掉，否则后续 AppendAsync 会拿着已释放的事务执行。
                if (_insertCommand is not null)
                {
                    _insertCommand.Transaction = null;
                }
            }

            return imported;
        }, cancellationToken);

    public Task<int> ReadBatchesAsync(
        ActivityQuery query,
        int batchSize,
        Func<IReadOnlyList<ActivityRecord>, CancellationToken, Task> onBatch,
        CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            var size = Math.Clamp(batchSize, 50, 5000);
            var (whereSql, parameters) = BuildWhere(query);
            var total = 0;
            var offset = 0;

            // 循环分页而不是一个长游标：数据库操作全程在同一个线程池任务里，
            // 但每批之间会把控制权交回调用方去写文件。
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<ActivityRecord> batch;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT {SelectColumns} FROM activity{whereSql} ORDER BY timestamp ASC, id ASC LIMIT $limit OFFSET $offset";
                    AddParameters(command, parameters);
                    command.Parameters.AddWithValue("$limit", size);
                    command.Parameters.AddWithValue("$offset", offset);

                    batch = [];
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        batch.Add(ReadRecord(reader));
                    }
                }

                if (batch.Count == 0)
                {
                    break;
                }

                onBatch(batch, cancellationToken).GetAwaiter().GetResult();
                total += batch.Count;
                offset += batch.Count;
            }

            return total;
        }, cancellationToken);

    public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM activity WHERE status <> 'Pending' AND timestamp < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString(TimeFormat, CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<int> ClearImagePathsBeforeAsync(DateTime cutoff, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE activity SET image_path = '' WHERE timestamp < $cutoff AND image_path <> ''";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString(TimeFormat, CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<IReadOnlyList<string>> DeleteAllAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(connection =>
        {
            var paths = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT image_path FROM activity WHERE image_path <> ''";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM activity";
                delete.ExecuteNonQuery();
            }

            return paths;
        }, cancellationToken);

    // ---------------------------------------------------------------- 定时批量识别

    public Task<IReadOnlyList<ActivityRecord>> GetPendingRecordsAsync(int limit, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<ActivityRecord>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {SelectColumns} FROM activity WHERE status = $status ORDER BY timestamp ASC, id ASC LIMIT $limit";
            command.Parameters.AddWithValue("$status", RecordStatus.Pending.ToString());
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

            var records = new List<ActivityRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    records.Add(ReadRecord(reader));
                }
            }

            return records;
        }, cancellationToken);

    public Task<int> CountPendingAsync(CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM activity WHERE status = $status";
            command.Parameters.AddWithValue("$status", RecordStatus.Pending.ToString());
            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task UpdateOcrResultAsync(
        long id,
        string text,
        long ocrMilliseconds,
        RecordStatus status,
        string error,
        CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE activity SET text = $text, ocr_ms = $ocrMs, status = $status, error = $error, " +
                "updated_at = $updatedAt WHERE id = $id";
            // 文字是这一天才补上的话，按天统计里的"最后改动时间"就会变，
            // 定时总结据此知道那一天的总结需要重新生成。
            command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$ocrMs", ocrMilliseconds);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        }, cancellationToken);

    // ---------------------------------------------------------------- 总结历史

    public Task AppendSummaryRunAsync(SummaryRun run, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO summary_runs
                    (started_at, finished_at, trigger, success, provider, attempts,
                     markdown, saved_path, message, record_count, image_count, elapsed_ms,
                     covered_day, covered_marks, covered_text_rev)
                VALUES
                    ($startedAt, $finishedAt, $trigger, $success, $provider, $attempts,
                     $markdown, $savedPath, $message, $recordCount, $imageCount, $elapsedMs,
                     $coveredDay, $coveredMarks, $coveredTextRev)
                """;
            command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString(TimeFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$finishedAt", run.FinishedAt.ToString(TimeFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$trigger", run.Trigger);
            command.Parameters.AddWithValue("$success", run.Success ? 1 : 0);
            command.Parameters.AddWithValue("$provider", run.Provider);
            command.Parameters.AddWithValue("$attempts", run.Attempts);
            command.Parameters.AddWithValue("$markdown", run.Markdown);
            command.Parameters.AddWithValue("$savedPath", run.SavedPath);
            command.Parameters.AddWithValue("$message", run.Message);
            command.Parameters.AddWithValue("$recordCount", run.RecordCount);
            command.Parameters.AddWithValue("$imageCount", run.ImageCount);
            command.Parameters.AddWithValue("$elapsedMs", run.ElapsedMilliseconds);
            command.Parameters.AddWithValue("$coveredDay", run.CoveredDay);
            command.Parameters.AddWithValue("$coveredMarks", run.CoveredMarks);
            command.Parameters.AddWithValue("$coveredTextRev", run.CoveredTextRevision);
            return command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<IReadOnlyList<SummaryRun>> GetSummaryRunsAsync(int limit, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<SummaryRun>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, started_at, finished_at, trigger, success, provider, attempts,
                       markdown, saved_path, message, record_count, image_count, elapsed_ms, pushed_at,
                       covered_day, covered_marks, covered_text_rev
                FROM summary_runs
                ORDER BY started_at DESC, id DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            var runs = new List<SummaryRun>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(ReadSummaryRun(reader));
            }

            return runs;
        }, cancellationToken);

    public Task<IReadOnlyList<SummaryRun>> GetPendingPushRunsAsync(
        DateTime from,
        int limit,
        CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<SummaryRun>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, started_at, finished_at, trigger, success, provider, attempts,
                       markdown, saved_path, message, record_count, image_count, elapsed_ms, pushed_at,
                       covered_day, covered_marks, covered_text_rev
                FROM summary_runs
                WHERE success = 1 AND pushed_at = '' AND markdown <> '' AND started_at >= $from
                ORDER BY started_at ASC, id ASC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$from", from.ToString(TimeFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            var runs = new List<SummaryRun>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(ReadSummaryRun(reader));
            }

            return runs;
        }, cancellationToken);

    public Task<int> CountPendingPushRunsAsync(DateTime from, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM summary_runs
                WHERE success = 1 AND pushed_at = '' AND markdown <> '' AND started_at >= $from
                """;
            command.Parameters.AddWithValue("$from", from.ToString(TimeFormat, CultureInfo.InvariantCulture));
            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> DeleteSummaryRunsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        return RunAsync<IReadOnlyList<string>>(connection =>
        {
            var placeholders = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                placeholders[i] = $"$id{i}";
            }

            var inClause = string.Join(", ", placeholders);

            var paths = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT saved_path FROM summary_runs WHERE id IN ({inClause})";
                for (var i = 0; i < ids.Count; i++)
                {
                    select.Parameters.AddWithValue(placeholders[i], ids[i]);
                }

                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = $"DELETE FROM summary_runs WHERE id IN ({inClause})";
                for (var i = 0; i < ids.Count; i++)
                {
                    delete.Parameters.AddWithValue(placeholders[i], ids[i]);
                }

                delete.ExecuteNonQuery();
            }

            return paths;
        }, cancellationToken);
    }

    public Task MarkSummaryRunsPushedAsync(
        IReadOnlyList<long> ids,
        DateTime pushedAt,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync(connection =>
        {
            using var command = connection.CreateCommand();

            // 参数化拼 IN：id 是数据库里读出来的整数，但照样不拼字符串。
            var names = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                names[i] = $"$id{i}";
                command.Parameters.AddWithValue(names[i], ids[i]);
            }

            command.CommandText =
                $"UPDATE summary_runs SET pushed_at = $pushedAt WHERE id IN ({string.Join(", ", names)})";
            command.Parameters.AddWithValue("$pushedAt", pushedAt.ToString(TimeFormat, CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }, cancellationToken);
    }

    private static SummaryRun ReadSummaryRun(SqliteDataReader reader)
    {
        var pushedAt = reader.GetString(13);
        return new SummaryRun
        {
            Id = reader.GetInt64(0),
            StartedAt = ParseTimestamp(reader.GetString(1)),
            FinishedAt = ParseTimestamp(reader.GetString(2)),
            Trigger = reader.GetString(3),
            Success = reader.GetInt32(4) != 0,
            Provider = reader.GetString(5),
            Attempts = reader.GetInt32(6),
            Markdown = reader.GetString(7),
            SavedPath = reader.GetString(8),
            Message = reader.GetString(9),
            RecordCount = reader.GetInt32(10),
            ImageCount = reader.GetInt32(11),
            ElapsedMilliseconds = reader.GetInt64(12),
            PushedAt = pushedAt.Length == 0 ? null : ParseTimestamp(pushedAt),
            CoveredDay = reader.GetString(14),
            CoveredMarks = reader.GetInt64(15),
            CoveredTextRevision = reader.GetString(16),
        };
    }

    public Task<int> DeleteSummaryRunsBeforeAsync(DateTime cutoff, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM summary_runs WHERE started_at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString(TimeFormat, CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<long> CountSummaryRunsAsync(CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM summary_runs";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> DeleteAllSummaryRunsAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(connection =>
        {
            var paths = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT saved_path FROM summary_runs WHERE saved_path <> ''";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM summary_runs";
                delete.ExecuteNonQuery();
            }

            return paths;
        }, cancellationToken);

    public Task<long> CountAsync(CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM activity";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }, cancellationToken);

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 串行 + 线程池。SQLite 的"异步"其实是同步的，
    /// 不放线程池的话导出几十万条会把 UI 卡死。
    /// </summary>
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _connection;
            if (connection is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                }.ToString());
                connection.Open();
                _connection = connection;
            }

            return await Task.Run(() => work(connection), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 幂等地补列。表名/列名/定义都来自本文件里的常量，不来自外部输入，所以可以直接拼进 SQL。
    /// </summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private static void ExecuteScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteScalar();
    }

    private void BindInsert(SqliteCommand command, ActivityRecord record)    {
        var p = command.Parameters;
        p["$timestamp"].Value = record.Timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture);
        p["$process"].Value = record.ProcessName;
        p["$title"].Value = record.WindowTitle;
        p["$windowClass"].Value = record.WindowClass;
        p["$text"].Value = record.OcrText;
        p["$ocrMs"].Value = record.OcrMilliseconds;
        p["$captureMethod"].Value = record.CaptureMethod;
        p["$imageWidth"].Value = record.ImageWidth;
        p["$imageHeight"].Value = record.ImageHeight;
        p["$imagePath"].Value = record.ImagePath;
        p["$status"].Value = record.Status.ToString();
        p["$error"].Value = record.Error;
        // 新建时"最后改动时间"就是抓取时间。
        p["$updatedAt"].Value = record.Timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    private static (string WhereSql, List<(string Name, object Value)> Parameters) BuildWhere(ActivityQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<(string, object)>();

        if (query.From is { } from)
        {
            conditions.Add("timestamp >= $from");
            parameters.Add(("$from", from.ToString(TimeFormat, CultureInfo.InvariantCulture)));
        }

        if (query.To is { } to)
        {
            conditions.Add("timestamp <= $to");
            parameters.Add(("$to", to.ToString(TimeFormat, CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessName))
        {
            conditions.Add("process = $process");
            parameters.Add(("$process", query.ProcessName.Trim()));
        }

        if (query.Status is { } status)
        {
            conditions.Add("status = $status");
            parameters.Add(("$status", status.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // 关键词搜索走 LIKE。没有上 FTS5：这里的数据量级下 LIKE 足够快，
            // 而 FTS5 要额外维护一张影子表，对"低资源占用"是负收益。
            // ESCAPE 子句不能省，否则用户输入的 % 和 _ 会被当成通配符。
            conditions.Add("(title LIKE $keyword ESCAPE '\\' OR text LIKE $keyword ESCAPE '\\')");
            parameters.Add(("$keyword", $"%{EscapeLike(query.Keyword.Trim())}%"));
        }

        var whereSql = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        return (whereSql, parameters);
    }

    /// <summary>把 LIKE 的通配符转义掉，否则用户输入 % 会匹配到所有记录。</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void AddParameters(SqliteCommand command, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static ActivityRecord ReadRecord(IDataRecord reader) => new()
    {
        Id = reader.GetInt64(0),
        Timestamp = ParseTimestamp(reader.GetString(1)),
        ProcessName = reader.GetString(2),
        WindowTitle = reader.GetString(3),
        WindowClass = reader.GetString(4),
        OcrText = reader.GetString(5),
        OcrMilliseconds = reader.GetInt64(6),
        CaptureMethod = reader.GetString(7),
        ImageWidth = reader.GetInt32(8),
        ImageHeight = reader.GetInt32(9),
        ImagePath = reader.GetString(10),
        Status = ParseStatus(reader.GetString(11)),
        Error = reader.GetString(12),
    };

    private static DateTime ParseTimestamp(string value) =>
        DateTime.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static RecordStatus ParseStatus(string value) =>
        Enum.TryParse<RecordStatus>(value, ignoreCase: true, out var status) ? status : RecordStatus.Ok;

    private static string Flatten(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is '\r' or '\n' ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                // WAL 收尾：把日志合并回主库，避免留下一堆 -wal 文件。
                using (var checkpoint = _connection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    try
                    {
                        checkpoint.ExecuteNonQuery();
                    }
                    catch (SqliteException)
                    {
                        // 收尾失败不影响正确性，下次打开会继续。
                    }
                }

                _insertCommand?.Dispose();
                _connection.Dispose();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

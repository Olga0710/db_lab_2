using System.Diagnostics;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;


static class Benchmark
{
    // ---------- параметри методики ----------
    const int Warmup = 5;       // прогрів для точкових запитів (не враховується)
    const int PointRuns = 100;  // повторень для S3, S4
    const int AggWarmup = 2;
    const int AggRuns = 15;     // повторень для агрегацій (вони важчі)

    static readonly DateTime From = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    static readonly DateTime To = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    static string CsvPath => Path.Combine(AppContext.BaseDirectory, "results.csv");

    // ---------- SQL-запити ----------
    // S3, варіант А: 4 запити (так працює типовий застосунок/ORM)
    const string HistVisits = """
        SELECT recordid, doctorid, visitdate, complaints, pulse, bloodpressure, temperature
        FROM "Medicalrecords"
        WHERE patientid = @p AND isdeleted = false
        ORDER BY visitdate DESC
        """;
    const string HistDiags = """
        SELECT rd.recordid, d.code, d.description, rd.isprimary
        FROM "Medicalrecords" mr
        JOIN recorddiagnoses rd ON rd.recordid = mr.recordid
        JOIN "Diagnoses" d ON d.diagnosisid = rd.diagnosisid
        WHERE mr.patientid = @p AND mr.isdeleted = false
        """;
    const string HistPresc = """
        SELECT p.recordid, m.tradename, m.activeingredient, m.price, p.dosage, p.durationdays
        FROM "Medicalrecords" mr
        JOIN "Prescriptions" p ON p.recordid = mr.recordid
        JOIN "Medicines" m ON m.medicineid = p.medicineid
        WHERE mr.patientid = @p AND mr.isdeleted = false AND p.isdeleted = false
        """;
    const string HistLabs = """
        SELECT l.recordid, l.testname, l.value, l.unit
        FROM "Medicalrecords" mr
        JOIN labresults l ON l.recordid = mr.recordid
        WHERE mr.patientid = @p AND mr.isdeleted = false AND l.isdeleted = false
        """;

    // S3, варіант Б: один запит, який збирає вкладені дані у JSON
    const string HistJson = """
        SELECT mr.recordid, mr.doctorid, mr.visitdate, mr.complaints, mr.pulse,
               mr.bloodpressure, mr.temperature,
          (SELECT COALESCE(json_agg(json_build_object('code', d.code, 'description', d.description,
                                                      'primary', rd.isprimary)), '[]'::json)
             FROM recorddiagnoses rd JOIN "Diagnoses" d ON d.diagnosisid = rd.diagnosisid
            WHERE rd.recordid = mr.recordid) AS diagnoses,
          (SELECT COALESCE(json_agg(json_build_object('trade_name', m.tradename, 'price', m.price,
                                                      'dosage', p.dosage, 'days', p.durationdays)), '[]'::json)
             FROM "Prescriptions" p JOIN "Medicines" m ON m.medicineid = p.medicineid
            WHERE p.recordid = mr.recordid AND p.isdeleted = false) AS prescriptions,
          (SELECT COALESCE(json_agg(json_build_object('test', l.testname, 'value', l.value,
                                                      'unit', l.unit)), '[]'::json)
             FROM labresults l
            WHERE l.recordid = mr.recordid AND l.isdeleted = false) AS lab_results
        FROM "Medicalrecords" mr
        WHERE mr.patientid = @p AND mr.isdeleted = false
        ORDER BY mr.visitdate DESC
        """;

    // S4а: візити з діагнозом X
    const string ByDiag = """
        SELECT mr.recordid
        FROM "Medicalrecords" mr
        JOIN recorddiagnoses rd ON rd.recordid = mr.recordid
        JOIN "Diagnoses" d ON d.diagnosisid = rd.diagnosisid
        WHERE d.code = @c AND mr.isdeleted = false
        """;

    // S4б: візити, де призначали ліки Y (DISTINCT: одні й ті ж ліки можуть бути призначені двічі в одному візиті)
    const string ByDrug = """
        SELECT DISTINCT mr.recordid
        FROM "Medicalrecords" mr
        JOIN "Prescriptions" p ON p.recordid = mr.recordid
        JOIN "Medicines" m ON m.medicineid = p.medicineid
        WHERE m.tradename = @n AND mr.isdeleted = false AND p.isdeleted = false
        """;

    // S5а: топ-10 діагнозів за 2025 рік
    const string TopDiag = """
        SELECT d.code, count(*) AS cnt
        FROM "Medicalrecords" mr
        JOIN recorddiagnoses rd ON rd.recordid = mr.recordid
        JOIN "Diagnoses" d ON d.diagnosisid = rd.diagnosisid
        WHERE mr.isdeleted = false AND mr.visitdate >= @d1 AND mr.visitdate < @d2
        GROUP BY d.code
        ORDER BY cnt DESC, d.code
        LIMIT 10
        """;

    // S5б: сума цін призначень по місяцях 2025 року
    const string MonthlySum = """
        SELECT date_trunc('month', mr.visitdate) AS mon, sum(m.price) AS total
        FROM "Medicalrecords" mr
        JOIN "Prescriptions" p ON p.recordid = mr.recordid
        JOIN "Medicines" m ON m.medicineid = p.medicineid
        WHERE mr.isdeleted = false AND p.isdeleted = false
          AND mr.visitdate >= @d1 AND mr.visitdate < @d2
        GROUP BY 1
        ORDER BY 1
        """;

    static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, params (string Name, object Value)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 600 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    static async Task<int> DrainAsync(NpgsqlConnection c, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = Cmd(c, sql, ps);
        await using var r = await cmd.ExecuteReaderAsync();
        int rows = 0;
        while (await r.ReadAsync())
        {
            for (int i = 0; i < r.FieldCount; i++) _ = r.GetValue(i);
            rows++;
        }
        return rows;
    }

    static async Task<List<T>> ListAsync<T>(NpgsqlConnection c, string sql)
    {
        var list = new List<T>();
        await using var cmd = Cmd(c, sql);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetFieldValue<T>(0));
        return list;
    }

    static async Task ExecAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = Cmd(c, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<(int V, int D, int P, int L)> PgHistoryAAsync(NpgsqlConnection c, int pid)
    {
        int v = await DrainAsync(c, HistVisits, ("p", pid));
        int d = await DrainAsync(c, HistDiags, ("p", pid));
        int p = await DrainAsync(c, HistPresc, ("p", pid));
        int l = await DrainAsync(c, HistLabs, ("p", pid));
        return (v, d, p, l);
    }

    static IMongoCollection<BsonDocument> GetColl() =>
        new MongoClient(Config.MongoConn).GetDatabase(Config.MongoDb)
            .GetCollection<BsonDocument>(Config.MongoColl);

    static async Task<(int V, int D, int P, int L)> MongoHistoryAsync(IMongoCollection<BsonDocument> coll, int pid)
    {
        var f = Builders<BsonDocument>.Filter;
        var docs = await coll.Find(f.And(f.Eq("patient_id", pid), f.Eq("is_deleted", false)))
                             .Sort(Builders<BsonDocument>.Sort.Descending("visit_date"))
                             .ToListAsync();
        int d = 0, p = 0, l = 0;
        foreach (var doc in docs)
        {
            d += doc["diagnoses"].AsBsonArray.Count;
            p += doc["prescriptions"].AsBsonArray.Count;
            l += doc["lab_results"].AsBsonArray.Count;
        }
        return (docs.Count, d, p, l);
    }

    static async Task<int> MongoByFieldAsync(IMongoCollection<BsonDocument> coll, string field, string value)
    {
        var f = Builders<BsonDocument>.Filter;
        var list = await coll.Find(f.And(f.Eq(field, value), f.Eq("is_deleted", false)))
                             .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("record_id").Exclude("_id"))
                             .ToListAsync();
        return list.Count;
    }

    static async Task<List<BsonDocument>> MongoTopDiagAsync(IMongoCollection<BsonDocument> coll)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "is_deleted", false },
                { "visit_date", new BsonDocument { { "$gte", DateTime.SpecifyKind(From, DateTimeKind.Utc) },
                                                   { "$lt",  DateTime.SpecifyKind(To, DateTimeKind.Utc) } } }
            }),
            new BsonDocument("$unwind", "$diagnoses"),
            new BsonDocument("$group", new BsonDocument { { "_id", "$diagnoses.code" },
                                                          { "cnt", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument { { "cnt", -1 }, { "_id", 1 } }),
            new BsonDocument("$limit", 10),
        };
        return await coll.Aggregate<BsonDocument>(pipeline).ToListAsync();
    }

    static async Task<List<BsonDocument>> MongoMonthlySumAsync(IMongoCollection<BsonDocument> coll)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "is_deleted", false },
                { "visit_date", new BsonDocument { { "$gte", DateTime.SpecifyKind(From, DateTimeKind.Utc) },
                                                   { "$lt",  DateTime.SpecifyKind(To, DateTimeKind.Utc) } } }
            }),
            new BsonDocument("$unwind", "$prescriptions"),
            new BsonDocument("$group", new BsonDocument { { "_id", new BsonDocument("$month", "$visit_date") },
                                                          { "total", new BsonDocument("$sum", "$prescriptions.price") } }),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };
        return await coll.Aggregate<BsonDocument>(pipeline).ToListAsync();
    }


    static readonly string[] PgIndexNames =
        { "ix_mr_patient_date", "ix_rd_diag", "ix_rd_record", "ix_presc_record",
          "ix_presc_med", "ix_lab_record", "ix_diag_code", "ix_med_name" };

    static async Task DropIndexesAsync(NpgsqlConnection pg, IMongoCollection<BsonDocument> coll)
    {
        foreach (var n in PgIndexNames) await ExecAsync(pg, $"DROP INDEX IF EXISTS {n}");
        await ExecAsync(pg, "ANALYZE \"Medicalrecords\"; ANALYZE recorddiagnoses; ANALYZE \"Prescriptions\"; ANALYZE labresults");
        foreach (var n in new[] { "patient_id_1_visit_date_-1", "diagnoses.code_1", "prescriptions.trade_name_1" })
        {
            try { await coll.Indexes.DropOneAsync(n); } catch { /* вже немає */ }
        }
    }

    static async Task CreateIndexesAsync(NpgsqlConnection pg, IMongoCollection<BsonDocument> coll)
    {
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_mr_patient_date ON \"Medicalrecords\"(patientid, visitdate)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_rd_diag ON recorddiagnoses(diagnosisid)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_rd_record ON recorddiagnoses(recordid)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_presc_record ON \"Prescriptions\"(recordid)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_presc_med ON \"Prescriptions\"(medicineid)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_lab_record ON labresults(recordid)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_diag_code ON \"Diagnoses\"(code)");
        await ExecAsync(pg, "CREATE INDEX IF NOT EXISTS ix_med_name ON \"Medicines\"(tradename)");
        await ExecAsync(pg, "ANALYZE \"Medicalrecords\"; ANALYZE recorddiagnoses; ANALYZE \"Prescriptions\"; ANALYZE labresults");

        var ik = Builders<BsonDocument>.IndexKeys;
        await coll.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ik.Ascending("patient_id").Descending("visit_date")));
        await coll.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ik.Ascending("diagnoses.code")));
        await coll.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ik.Ascending("prescriptions.trade_name")));
    }

    record Stat(double Median, double P95, double Mean, double Min, double Max);

    static Stat MakeStat(double[] t)
    {
        var s = t.OrderBy(x => x).ToArray();
        int n = s.Length;
        double p95 = s[Math.Max(0, (int)Math.Ceiling(0.95 * n) - 1)];
        double median = n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
        return new Stat(median, p95, s.Average(), s[0], s[n - 1]);
    }

    static string F(double x) => x.ToString("F3", CultureInfo.InvariantCulture);

    static async Task ScenarioAsync(string name, string db, int warmup, int runs,
                                    Func<int, Task> action, long size, string mode)
    {
        for (int i = 0; i < warmup; i++) await action(i);

        var times = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            await action(warmup + i);
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        var s = MakeStat(times);

        Console.WriteLine($"{name,-24} {db,-26} медіана {s.Median,9:F2} мс | p95 {s.P95,9:F2} мс | n={runs}");

        if (!File.Exists(CsvPath))
            File.WriteAllText(CsvPath, "visits;mode;scenario;db;runs;median_ms;p95_ms;mean_ms;min_ms;max_ms" + Environment.NewLine);
        File.AppendAllText(CsvPath,
            string.Join(";", size, mode, name, db, runs, F(s.Median), F(s.P95), F(s.Mean), F(s.Min), F(s.Max))
            + Environment.NewLine);
    }


    static async Task<bool> VerifyAsync(NpgsqlConnection pg, IMongoCollection<BsonDocument> coll,
                                        int[] patients, string[] codes, string[] drugs)
    {
        Console.WriteLine("Перевірка однаковості результатів...");
        int bad = 0;

        foreach (var pid in patients.Distinct().Take(100))
        {
            var a = await PgHistoryAAsync(pg, pid);
            var b = await MongoHistoryAsync(coll, pid);
            if (a != b) { bad++; Console.WriteLine($"  РІЗНІ історії пацієнта {pid}: PG {a}, Mongo {b}"); }
        }
        foreach (var code in codes.Distinct().Take(30))
        {
            int a = await DrainAsync(pg, ByDiag, ("c", code));
            int b = await MongoByFieldAsync(coll, "diagnoses.code", code);
            if (a != b) { bad++; Console.WriteLine($"  РІЗНІ візити з діагнозом {code}: PG {a}, Mongo {b}"); }
        }
        foreach (var drug in drugs.Distinct().Take(30))
        {
            int a = await DrainAsync(pg, ByDrug, ("n", drug));
            int b = await MongoByFieldAsync(coll, "prescriptions.trade_name", drug);
            if (a != b) { bad++; Console.WriteLine($"  РІЗНІ візити з ліками {drug}: PG {a}, Mongo {b}"); }
        }

        // агрегація: порівнюємо топ-10 діагнозів
        var pgTop = new List<(string, long)>();
        await using (var cmd = Cmd(pg, TopDiag, ("d1", From), ("d2", To)))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) pgTop.Add((r.GetString(0), r.GetInt64(1)));
        var mgTop = (await MongoTopDiagAsync(coll))
            .Select(x => (x["_id"].AsString, (long)x["cnt"].ToInt64())).ToList();
        if (!pgTop.SequenceEqual(mgTop)) { bad++; Console.WriteLine("  РІЗНІ топ-10 діагнозів"); }

        Console.WriteLine(bad == 0 ? "  OK: результати збігаються." : $"  Знайдено розбіжностей: {bad}");
        return bad == 0;
    }

   
    static async Task RunSetAsync(NpgsqlConnection pg, IMongoCollection<BsonDocument> coll,
                                  int[] patients, string[] codes, string[] drugs,
                                  long size, string mode, bool includeAgg)
    {
        
        bool heavy = mode == "noindex" && size >= 500000;
        int w = heavy ? 2 : Warmup;
        int n = heavy ? 20 : PointRuns;
        if (heavy) Console.WriteLine("  (великий обсяг без індексів: 20 повторень, варіант JSON пропущено)");

        await ScenarioAsync("S3 історія пацієнта", "PostgreSQL (4 запити)", w, n,
            i => PgHistoryAAsync(pg, patients[i % patients.Length]), size, mode);
        if (!heavy)
            await ScenarioAsync("S3 історія пацієнта", "PostgreSQL (1 запит JSON)", w, n,
                i => DrainAsync(pg, HistJson, ("p", patients[i % patients.Length])), size, mode);
        await ScenarioAsync("S3 історія пацієнта", "MongoDB", w, n,
            i => MongoHistoryAsync(coll, patients[i % patients.Length]), size, mode);

        await ScenarioAsync("S4а пошук за діагнозом", "PostgreSQL", w, n,
            i => DrainAsync(pg, ByDiag, ("c", codes[i % codes.Length])), size, mode);
        await ScenarioAsync("S4а пошук за діагнозом", "MongoDB", w, n,
            i => MongoByFieldAsync(coll, "diagnoses.code", codes[i % codes.Length]), size, mode);

        await ScenarioAsync("S4б пошук за ліками", "PostgreSQL", w, n,
            i => DrainAsync(pg, ByDrug, ("n", drugs[i % drugs.Length])), size, mode);
        await ScenarioAsync("S4б пошук за ліками", "MongoDB", w, n,
            i => MongoByFieldAsync(coll, "prescriptions.trade_name", drugs[i % drugs.Length]), size, mode);

        if (!includeAgg) return;

        await ScenarioAsync("S5а топ-10 діагнозів", "PostgreSQL", AggWarmup, AggRuns,
            i => DrainAsync(pg, TopDiag, ("d1", From), ("d2", To)), size, mode);
        await ScenarioAsync("S5а топ-10 діагнозів", "MongoDB", AggWarmup, AggRuns,
            i => MongoTopDiagAsync(coll), size, mode);

        await ScenarioAsync("S5б сума цін по місяцях", "PostgreSQL", AggWarmup, AggRuns,
            i => DrainAsync(pg, MonthlySum, ("d1", From), ("d2", To)), size, mode);
        await ScenarioAsync("S5б сума цін по місяцях", "MongoDB", AggWarmup, AggRuns,
            i => MongoMonthlySumAsync(coll), size, mode);
    }

    static T[] Sample<T>(List<T> source, int n, Random rnd)
    {
        var res = new T[n];
        for (int i = 0; i < n; i++) res[i] = source[rnd.Next(source.Count)];
        return res;
    }

    public static async Task RunReadsAsync()
    {
        await using var pg = new NpgsqlConnection(Config.PgConn);
        await pg.OpenAsync();
        var coll = GetColl();

        var sizeList = await ListAsync<long>(pg, "SELECT count(*) FROM \"Medicalrecords\"");
        long size = sizeList[0];
        long mongoSize = await coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        if (size == 0 || size != mongoSize)
        {
            Console.WriteLine($"Потрібні однакові непорожні дані: PostgreSQL {size}, MongoDB {mongoSize}.");
            return;
        }

        var rnd = new Random(2024);
        var withVisits = await ListAsync<int>(pg,
            "SELECT DISTINCT patientid FROM \"Medicalrecords\" WHERE isdeleted = false ORDER BY patientid");
        var patients = Sample(withVisits, 200, rnd);
        var codes = Sample(await ListAsync<string>(pg, "SELECT code FROM \"Diagnoses\" ORDER BY diagnosisid"), 200, rnd);
        var drugs = Sample(await ListAsync<string>(pg, "SELECT tradename FROM \"Medicines\" ORDER BY medicineid"), 200, rnd);

        if (!await VerifyAsync(pg, coll, patients, codes, drugs)) return;

        Console.WriteLine();
        Console.WriteLine($"=== Візитів: {size}. З ІНДЕКСАМИ ===");
        await RunSetAsync(pg, coll, patients, codes, drugs, size, "indexed", includeAgg: true);

        Console.WriteLine();
        Console.WriteLine($"=== Візитів: {size}. БЕЗ ІНДЕКСІВ (S3, S4) ===");
        try
        {
            await DropIndexesAsync(pg, coll);
            await RunSetAsync(pg, coll, patients, codes, drugs, size, "noindex", includeAgg: false);
        }
        finally
        {
            await CreateIndexesAsync(pg, coll);  
            Console.WriteLine("Індекси відновлено.");
        }

        Console.WriteLine();
        Console.WriteLine("Результати збережено: " + CsvPath);
    }
}
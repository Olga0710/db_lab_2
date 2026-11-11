using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using NpgsqlTypes;


static class LoadVisits
{
    record Diag(int Id, string Code, string Description);
    record Med(int Id, string Name, string Ingredient, decimal Price);
    record Ref(int[] Patients, int[] Doctors, Diag[] Diags, Med[] Meds);

    record DiagItem(int Id, string Code, string Description, bool Primary);
    record PrescItem(int MedId, string Name, string Ingredient, decimal Price, string Dosage, int Days);
    record LabItem(string Test, decimal Value, string Unit);

    class Visit
    {
        public int RecordId, PatientId, DoctorId, Pulse;
        public DateTime VisitDate;
        public string Complaints = "", Bp = "";
        public decimal Temp;
        public List<DiagItem> Diags = new();
        public List<PrescItem> Presc = new();
        public List<LabItem> Labs = new();
    }

    static readonly (string Name, string Unit, double Min, double Max)[] Tests =
    {
        ("Холестерин", "ммоль/л", 3.0, 8.0), ("Глюкоза", "ммоль/л", 3.5, 9.0),
        ("Гемоглобін", "г/л", 100, 170),     ("Лейкоцити", "10^9/л", 3.5, 12.0),
        ("Еритроцити", "10^12/л", 3.5, 5.8), ("Тромбоцити", "10^9/л", 150, 400),
        ("Креатинін", "мкмоль/л", 50, 130),  ("Сечовина", "ммоль/л", 2.5, 8.5),
        ("АЛТ", "Од/л", 10, 80),             ("АСТ", "Од/л", 10, 80),
        ("Білірубін", "мкмоль/л", 3, 25),    ("Калій", "ммоль/л", 3.2, 5.5),
        ("Натрій", "ммоль/л", 130, 150),     ("СРБ", "мг/л", 0, 40),
        ("ТТГ", "мОд/л", 0.2, 6.0),          ("Залізо", "мкмоль/л", 5, 30),
        ("Вітамін D", "нг/мл", 10, 60),      ("Ферритин", "нг/мл", 10, 300),
        ("ШОЕ", "мм/год", 2, 40),            ("Тригліцериди", "ммоль/л", 0.5, 3.5),
    };

    static readonly string[] Complaints =
    {
        "біль у грудях", "головний біль", "підвищена температура", "кашель", "біль у животі",
        "запаморочення", "слабкість", "біль у спині", "задишка", "нудота",
        "біль у горлі", "біль у суглобах", "підвищений тиск", "безсоння", "висипання на шкірі",
    };

    static readonly string[] Dosages =
        { "50 мг", "100 мг", "250 мг", "500 мг", "1 таб. 2 р/д", "1 таб. 3 р/д", "5 мл 3 р/д", "10 мг 1 р/д" };

    static readonly DateTime BaseDate = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    const int RangeDays = 3 * 365;
    const int Batch = 5000;

    static async Task<long> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    static async Task ExecAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync();
    }

    static IMongoCollection<BsonDocument> GetColl() =>
        new MongoClient(Config.MongoConn).GetDatabase(Config.MongoDb)
            .GetCollection<BsonDocument>(Config.MongoColl);

    static async Task<Ref> LoadRefAsync(NpgsqlConnection c)
    {
        var patients = new List<int>();
        var doctors = new List<int>();
        var diags = new List<Diag>();
        var meds = new List<Med>();

        await using (var cmd = new NpgsqlCommand("SELECT patientid FROM \"Patients\" WHERE isdeleted = false ORDER BY patientid", c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) patients.Add(r.GetInt32(0));

        await using (var cmd = new NpgsqlCommand("SELECT doctorid FROM \"Doctors\" WHERE isdeleted = false ORDER BY doctorid", c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) doctors.Add(r.GetInt32(0));

        await using (var cmd = new NpgsqlCommand("SELECT diagnosisid, code, description FROM \"Diagnoses\" ORDER BY diagnosisid", c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) diags.Add(new Diag(r.GetInt32(0), r.GetString(1), r.GetString(2)));

        await using (var cmd = new NpgsqlCommand("SELECT medicineid, tradename, COALESCE(activeingredient,''), COALESCE(price,0) FROM \"Medicines\" ORDER BY medicineid", c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) meds.Add(new Med(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDecimal(3)));

        return new Ref(patients.ToArray(), doctors.ToArray(), diags.ToArray(), meds.ToArray());
    }

    static Visit Generate(int id, Ref d)
    {
        var r = new Random(unchecked((int)((uint)id * 2654435761u)));
        var v = new Visit
        {
            RecordId = id,
            PatientId = d.Patients[r.Next(d.Patients.Length)],
            DoctorId = d.Doctors[r.Next(d.Doctors.Length)],
            VisitDate = BaseDate.AddDays(r.Next(RangeDays)).AddMinutes(r.Next(8 * 60, 18 * 60)),
            Complaints = Complaints[r.Next(Complaints.Length)],
            Pulse = r.Next(55, 121),
            Bp = $"{r.Next(100, 161)}/{r.Next(60, 101)}",
            Temp = Math.Round((decimal)(36.0 + r.NextDouble() * 3.5), 1),
        };

        int roll = r.Next(100);
        int nd = roll < 50 ? 1 : roll < 85 ? 2 : 3;
        var used = new HashSet<int>();
        while (v.Diags.Count < nd)
        {
            var dg = d.Diags[r.Next(d.Diags.Length)];
            if (!used.Add(dg.Id)) continue;
            v.Diags.Add(new DiagItem(dg.Id, dg.Code, dg.Description, v.Diags.Count == 0));
        }

        // 0-5 призначень
        int np = r.Next(0, 6);
        for (int i = 0; i < np; i++)
        {
            var m = d.Meds[r.Next(d.Meds.Length)];
            v.Presc.Add(new PrescItem(m.Id, m.Name, m.Ingredient, m.Price,
                                      Dosages[r.Next(Dosages.Length)], r.Next(3, 61)));
        }

        // 0-10 аналізів
        int nl = r.Next(0, 11);
        for (int i = 0; i < nl; i++)
        {
            var t = Tests[r.Next(Tests.Length)];
            v.Labs.Add(new LabItem(t.Name,
                                   Math.Round((decimal)(t.Min + r.NextDouble() * (t.Max - t.Min)), 2),
                                   t.Unit));
        }
        return v;
    }

    
    static async Task InsertPgAsync(NpgsqlConnection c, List<Visit> batch)
    {
        await using var tx = await c.BeginTransactionAsync();

        await using (var w = await c.BeginBinaryImportAsync(
            "COPY \"Medicalrecords\" (recordid, patientid, doctorid, visitdate, complaints, pulse, " +
            "bloodpressure, temperature, notes, isdeleted, updatedat, updatedby) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var v in batch)
            {
                await w.StartRowAsync();
                await w.WriteAsync(v.RecordId, NpgsqlDbType.Integer);
                await w.WriteAsync(v.PatientId, NpgsqlDbType.Integer);
                await w.WriteAsync(v.DoctorId, NpgsqlDbType.Integer);
                await w.WriteAsync(v.VisitDate, NpgsqlDbType.Timestamp);
                await w.WriteAsync(v.Complaints, NpgsqlDbType.Text);
                await w.WriteAsync(v.Pulse, NpgsqlDbType.Integer);
                await w.WriteAsync(v.Bp, NpgsqlDbType.Varchar);
                await w.WriteAsync(v.Temp, NpgsqlDbType.Numeric);
                await w.WriteNullAsync();                                  
                await w.WriteAsync(false, NpgsqlDbType.Boolean);
                await w.WriteAsync(v.VisitDate, NpgsqlDbType.Timestamp);   
                await w.WriteAsync("generator", NpgsqlDbType.Varchar);
            }
            await w.CompleteAsync();
        }

        await using (var w = await c.BeginBinaryImportAsync(
            "COPY recorddiagnoses (recordid, diagnosisid, isprimary) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var v in batch)
                foreach (var x in v.Diags)
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(v.RecordId, NpgsqlDbType.Integer);
                    await w.WriteAsync(x.Id, NpgsqlDbType.Integer);
                    await w.WriteAsync(x.Primary, NpgsqlDbType.Boolean);
                }
            await w.CompleteAsync();
        }

        await using (var w = await c.BeginBinaryImportAsync(
            "COPY \"Prescriptions\" (recordid, medicineid, dosage, durationdays, isdeleted) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var v in batch)
                foreach (var x in v.Presc)
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(v.RecordId, NpgsqlDbType.Integer);
                    await w.WriteAsync(x.MedId, NpgsqlDbType.Integer);
                    await w.WriteAsync(x.Dosage, NpgsqlDbType.Varchar);
                    await w.WriteAsync(x.Days, NpgsqlDbType.Integer);
                    await w.WriteAsync(false, NpgsqlDbType.Boolean);
                }
            await w.CompleteAsync();
        }

        await using (var w = await c.BeginBinaryImportAsync(
            "COPY labresults (recordid, testname, value, unit, isdeleted) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var v in batch)
                foreach (var x in v.Labs)
                {
                    await w.StartRowAsync();
                    await w.WriteAsync(v.RecordId, NpgsqlDbType.Integer);
                    await w.WriteAsync(x.Test, NpgsqlDbType.Varchar);
                    await w.WriteAsync(x.Value, NpgsqlDbType.Numeric);
                    await w.WriteAsync(x.Unit, NpgsqlDbType.Varchar);
                    await w.WriteAsync(false, NpgsqlDbType.Boolean);
                }
            await w.CompleteAsync();
        }

        await tx.CommitAsync();
    }

    static BsonDocument ToDoc(Visit v) => new BsonDocument
    {
        { "record_id", v.RecordId },
        { "patient_id", v.PatientId },
        { "doctor_id", v.DoctorId },
        { "visit_date", DateTime.SpecifyKind(v.VisitDate, DateTimeKind.Utc) },
        { "complaints", v.Complaints },
        { "vitals", new BsonDocument { { "pulse", v.Pulse }, { "bp", v.Bp }, { "temp", (double)v.Temp } } },
        { "diagnoses", new BsonArray(v.Diags.Select(x => (BsonValue)new BsonDocument {
            { "diagnosis_id", x.Id }, { "code", x.Code },
            { "description", x.Description }, { "primary", x.Primary } })) },
        { "prescriptions", new BsonArray(v.Presc.Select(x => (BsonValue)new BsonDocument {
            { "medicine_id", x.MedId }, { "trade_name", x.Name },
            { "active_ingredient", x.Ingredient }, { "price", (double)x.Price },
            { "dosage", x.Dosage }, { "duration_days", x.Days } })) },
        { "lab_results", new BsonArray(v.Labs.Select(x => (BsonValue)new BsonDocument {
            { "test", x.Test }, { "value", (double)x.Value }, { "unit", x.Unit } })) },
        { "is_deleted", false },
        { "updated_at", DateTime.SpecifyKind(v.VisitDate, DateTimeKind.Utc) },
        { "updated_by", "generator" },
    };

  
    public static async Task RunAsync(int target)
    {
        await using var pg = new NpgsqlConnection(Config.PgConn);
        await pg.OpenAsync();
        var coll = GetColl();

        long pgCnt = await ScalarAsync(pg, "SELECT count(*) FROM \"Medicalrecords\"");
        long pgMax = await ScalarAsync(pg, "SELECT COALESCE(max(recordid), 0) FROM \"Medicalrecords\"");
        long mongoCnt = await coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

        if (pgCnt != mongoCnt || pgMax != pgCnt)
        {
            Console.WriteLine($"Бази неузгоджені: PostgreSQL {pgCnt} (max id {pgMax}), MongoDB {mongoCnt}.");
            Console.WriteLine("Виконай пункт 4 (очистити візити) і повтори.");
            return;
        }

        int start = (int)pgCnt + 1;
        if (target < start)
        {
            Console.WriteLine($"Уже є {pgCnt} візитів, це не менше за {target}. Нічого робити.");
            return;
        }

        var d = await LoadRefAsync(pg);
        Console.WriteLine($"Довідники: пацієнтів {d.Patients.Length}, лікарів {d.Doctors.Length}, " +
                          $"діагнозів {d.Diags.Length}, ліків {d.Meds.Length}");
        Console.WriteLine($"Додаю візити {start}..{target} в обидві бази...");

        var total = Stopwatch.StartNew();
        long pgMs = 0, mongoMs = 0;
        int batches = 0;

        for (int from = start; from <= target; from += Batch)
        {
            int to = Math.Min(from + Batch - 1, target);
            var batch = new List<Visit>(to - from + 1);
            for (int i = from; i <= to; i++) batch.Add(Generate(i, d));

            var t = Stopwatch.StartNew();
            await InsertPgAsync(pg, batch);
            pgMs += t.ElapsedMilliseconds;

            t.Restart();
            await coll.InsertManyAsync(batch.Select(ToDoc));
            mongoMs += t.ElapsedMilliseconds;

            batches++;
            if (batches % 10 == 0 || to == target)
                Console.WriteLine($"  ... {to} / {target}");
        }

        await ExecAsync(pg,
            "SELECT setval(pg_get_serial_sequence('\"Medicalrecords\"', 'recordid'), " +
            "(SELECT max(recordid) FROM \"Medicalrecords\"))");
        await ExecAsync(pg,
            "ANALYZE \"Medicalrecords\"; ANALYZE recorddiagnoses; ANALYZE \"Prescriptions\"; ANALYZE labresults;");

        Console.WriteLine($"Готово за {total.Elapsed.TotalSeconds:F1} с " +
                          $"(PostgreSQL {pgMs / 1000.0:F1} с, MongoDB {mongoMs / 1000.0:F1} с, без генерації).");
    }

    public static async Task ShowCountsAsync()
    {
        await using var pg = new NpgsqlConnection(Config.PgConn);
        await pg.OpenAsync();
        Console.WriteLine("PostgreSQL:");
        foreach (var t in new[] { "\"Medicalrecords\"", "recorddiagnoses", "\"Prescriptions\"", "labresults" })
            Console.WriteLine($"  {t}: {await ScalarAsync(pg, $"SELECT count(*) FROM {t}")}");
        long m = await GetColl().CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Console.WriteLine($"MongoDB medical_records: {m}");
    }

    public static async Task ClearAsync()
    {
        await using var pg = new NpgsqlConnection(Config.PgConn);
        await pg.OpenAsync();
        await ExecAsync(pg,
            "TRUNCATE \"Medicalrecords\", recorddiagnoses, \"Prescriptions\", labresults RESTART IDENTITY");
        await GetColl().DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        Console.WriteLine("Візити очищено в обох базах (довідники не чіпали).");
    }
}
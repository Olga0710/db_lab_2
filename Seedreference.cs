using Bogus;
using Npgsql;
using NpgsqlTypes;

static class SeedReference
{
    const int TargetPatients = 20000;
    const int TargetDoctors = 100;
    const int TargetDiagnoses = 500;
    const int TargetMedicines = 1000;

    static async Task<long> CountAsync(NpgsqlConnection c, string table)
    {
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    static async Task<List<int>> IdsAsync(NpgsqlConnection c, string table, string col)
    {
        var list = new List<int>();
        await using var cmd = new NpgsqlCommand($"SELECT {col} FROM \"{table}\"", c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetInt32(0));
        return list;
    }

    static async Task<HashSet<string>> StringsAsync(NpgsqlConnection c, string table, string col)
    {
        var set = new HashSet<string>();
        await using var cmd = new NpgsqlCommand($"SELECT {col} FROM \"{table}\"", c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

    public static async Task RunAsync()
    {
        Randomizer.Seed = new Random(42);
        var rnd = new Random(42);
        var faker = new Faker("uk");

        await using var conn = new NpgsqlConnection(Config.PgConn);
        await conn.OpenAsync();

        long have = await CountAsync(conn, "Patients");
        int need = (int)(TargetPatients - have);
        if (need > 0)
        {
            await using var w = await conn.BeginBinaryImportAsync(
                "COPY \"Patients\" (firstname, lastname, dateofbirth, gender, phone, address, isdeleted) " +
                "FROM STDIN (FORMAT BINARY)");
            for (int i = 0; i < need; i++)
            {
                await w.StartRowAsync();
                await w.WriteAsync(faker.Name.FirstName(), NpgsqlDbType.Varchar);
                await w.WriteAsync(faker.Name.LastName(), NpgsqlDbType.Varchar);
                await w.WriteAsync(DateOnly.FromDateTime(faker.Date.Past(90, DateTime.Today.AddYears(-1))),
                                   NpgsqlDbType.Date);
                await w.WriteAsync(rnd.Next(2) == 0 ? "M" : "F", NpgsqlDbType.Varchar);
                await w.WriteAsync(faker.Phone.PhoneNumber("+380#########"), NpgsqlDbType.Varchar);
                await w.WriteAsync(faker.Address.City() + ", " + faker.Address.StreetAddress(), NpgsqlDbType.Text);
                await w.WriteAsync(false, NpgsqlDbType.Boolean);
            }
            await w.CompleteAsync();
        }
        Console.WriteLine($"Patients: було {have}, додано {Math.Max(need, 0)}");

        var deptIds = await IdsAsync(conn, "Departments", "departmentid");
        var specIds = await IdsAsync(conn, "Specializations", "specializationid");

        have = await CountAsync(conn, "Doctors");
        need = (int)(TargetDoctors - have);
        if (need > 0)
        {
            await using var w = await conn.BeginBinaryImportAsync(
                "COPY \"Doctors\" (departmentid, specializationid, firstname, lastname, phone, isdeleted) " +
                "FROM STDIN (FORMAT BINARY)");
            for (int i = 0; i < need; i++)
            {
                await w.StartRowAsync();
                if (deptIds.Count > 0) await w.WriteAsync(deptIds[rnd.Next(deptIds.Count)], NpgsqlDbType.Integer);
                else await w.WriteNullAsync();
                if (specIds.Count > 0) await w.WriteAsync(specIds[rnd.Next(specIds.Count)], NpgsqlDbType.Integer);
                else await w.WriteNullAsync();
                await w.WriteAsync(faker.Name.FirstName(), NpgsqlDbType.Varchar);
                await w.WriteAsync(faker.Name.LastName(), NpgsqlDbType.Varchar);
                await w.WriteAsync(faker.Phone.PhoneNumber("+380#########"), NpgsqlDbType.Varchar);
                await w.WriteAsync(false, NpgsqlDbType.Boolean);
            }
            await w.CompleteAsync();
        }
        Console.WriteLine($"Doctors: було {have}, додано {Math.Max(need, 0)}");


        var usedCodes = await StringsAsync(conn, "Diagnoses", "code");
        have = await CountAsync(conn, "Diagnoses");
        need = (int)(TargetDiagnoses - have);
        if (need > 0)
        {
            await using var w = await conn.BeginBinaryImportAsync(
                "COPY \"Diagnoses\" (code, description) FROM STDIN (FORMAT BINARY)");
            int added = 0;
            while (added < need)
            {
                string code = $"{(char)('A' + rnd.Next(26))}{rnd.Next(100):00}.{rnd.Next(10)}";
                if (!usedCodes.Add(code)) continue;
                await w.StartRowAsync();
                await w.WriteAsync(code, NpgsqlDbType.Varchar);
                await w.WriteAsync($"Захворювання {code}", NpgsqlDbType.Text);
                added++;
            }
            await w.CompleteAsync();
        }
        Console.WriteLine($"Diagnoses: було {have}, додано {Math.Max(need, 0)}");

      
        string[] syl = { "ам", "бе", "ци", "до", "ер", "фа", "гі", "ка", "ло", "ме",
                         "нор", "ок", "пре", "ре", "сул", "тра", "ум", "ві", "зол", "кс" };
        string MakeName()
        {
            int n = rnd.Next(2, 5);
            string s = "";
            for (int i = 0; i < n; i++) s += syl[rnd.Next(syl.Length)];
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        var usedNames = await StringsAsync(conn, "Medicines", "tradename");
        have = await CountAsync(conn, "Medicines");
        need = (int)(TargetMedicines - have);
        if (need > 0)
        {
            await using var w = await conn.BeginBinaryImportAsync(
                "COPY \"Medicines\" (tradename, activeingredient, price) FROM STDIN (FORMAT BINARY)");
            int added = 0;
            while (added < need)
            {
                string name = MakeName();
                if (!usedNames.Add(name)) continue;
                await w.StartRowAsync();
                await w.WriteAsync(name, NpgsqlDbType.Varchar);
                await w.WriteAsync(MakeName() + "ин", NpgsqlDbType.Varchar);
                await w.WriteAsync(Math.Round((decimal)(rnd.NextDouble() * 1990 + 10), 2), NpgsqlDbType.Numeric);
                added++;
            }
            await w.CompleteAsync();
        }
        Console.WriteLine($"Medicines: було {have}, додано {Math.Max(need, 0)}");
        Console.WriteLine("Довідники готові.");
    }
}
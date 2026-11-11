Console.OutputEncoding = System.Text.Encoding.UTF8;

while (true)
{
    Console.WriteLine();
    Console.WriteLine("1 - довідники (пацієнти, лікарі, діагнози, ліки)");
    Console.WriteLine("2 - завантажити візити в обидві бази (до вказаної кількості)");
    Console.WriteLine("3 - показати кількість записів");
    Console.WriteLine("4 - ОЧИСТИТИ візити в обох базах");
    Console.WriteLine("5 - бенчмарк читання (історія, пошук, агрегації)");
    Console.WriteLine("0 - вихід");
    Console.Write("> ");

    var cmd = Console.ReadLine()?.Trim();
    try
    {
        switch (cmd)
        {
            case "1":
                await SeedReference.RunAsync();
                break;
            case "2":
                Console.Write("До якої кількості візитів? (наприклад 10000): ");
                if (int.TryParse(Console.ReadLine(), out int n) && n > 0)
                    await LoadVisits.RunAsync(n);
                else
                    Console.WriteLine("Потрібне число більше за 0.");
                break;
            case "3":
                await LoadVisits.ShowCountsAsync();
                break;
            case "4":
                Console.Write("Точно очистити візити? (так/ні): ");
                if (Console.ReadLine()?.Trim().ToLower() == "так")
                    await LoadVisits.ClearAsync();
                break;
            case "5":
                await Benchmark.RunReadsAsync();
                break;
            case "0":
                return;
        }
    }
    catch (Exception e)
    {
        Console.WriteLine("ПОМИЛКА: " + e.Message);
    }
}
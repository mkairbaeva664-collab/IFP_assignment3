using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChainMonitor;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageClass { Cold, Frozen }

public sealed record Reading(string SensorId, DateTimeOffset Timestamp, StorageClass StorageClass, decimal Temperature);
public sealed record ImportError(int LineNumber, string RawLine, string Message);
public sealed record Alert(string SensorId, DateTimeOffset Timestamp, decimal Temperature, IReadOnlyList<string> Reasons);
public sealed record MonitoringArchive(DateTimeOffset CreatedAtUtc, IReadOnlyList<Reading> Readings, IReadOnlyList<ImportError> Errors, IReadOnlyList<Alert> Alerts);
public sealed record ImportResult(IReadOnlyList<Reading> Readings, IReadOnlyList<ImportError> Errors);

public static class ColdChainLogic
{
    public static bool TryParseReading(string line, out Reading? reading, out string? errorMessage)
    {
        reading = null; errorMessage = null;
        if (string.IsNullOrWhiteSpace(line)) { errorMessage = "Empty line."; return false; }

        var parts = line.Split('|');
        if (parts.Length != 4) { errorMessage = $"Expected 4 fields, got {parts.Length}."; return false; }

        string sensorId = parts[0].Trim(), ts = parts[1].Trim(), sc = parts[2].Trim(), temp = parts[3].Trim();
        if (string.IsNullOrEmpty(sensorId)) { errorMessage = "Empty sensor ID."; return false; }

        if (!DateTimeOffset.TryParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
        { errorMessage = "Invalid timestamp format or offset."; return false; }

        if (!Enum.TryParse<StorageClass>(sc, true, out var storageClass) || !Enum.IsDefined(typeof(StorageClass), storageClass))
        { errorMessage = $"Invalid StorageClass: '{sc}'."; return false; }

        if (!decimal.TryParse(temp, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var temperature))
        { errorMessage = $"Invalid temperature format: '{temp}'."; return false; }

        reading = new Reading(sensorId, timestamp, storageClass, temperature);
        return true;
    }

    public static ImportResult ReadReadings(Stream input)
    {
        using var reader = new StreamReader(input, leaveOpen: true);
        var readings = new List<Reading>();
        var errors = new List<ImportError>();
        var sensorClasses = new Dictionary<string, StorageClass>(StringComparer.OrdinalIgnoreCase);

        int lineNum = 0; string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!TryParseReading(line, out var r, out var err))
            {
                errors.Add(new ImportError(lineNum, line, err!));
                continue;
            }

            if (sensorClasses.TryGetValue(r!.SensorId, out var existingClass))
            {
                if (existingClass != r.StorageClass)
                {
                    errors.Add(new ImportError(lineNum, line, $"StorageClass mismatch for sensor '{r.SensorId}'. Expected {existingClass}, got {r.StorageClass}."));
                    continue;
                }
            }
            else sensorClasses[r.SensorId] = r.StorageClass;

            readings.Add(r);
        }
        return new ImportResult(readings.AsReadOnly(), errors.AsReadOnly());
    }

    public static IReadOnlyList<Alert> Analyze(IEnumerable<Reading> readings)
    {
        var alerts = new List<Alert>();
        var canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in readings)
        {
            if (!canonicalNames.ContainsKey(r.SensorId))
                canonicalNames[r.SensorId] = r.SensorId;
        }

        foreach (var group in readings.GroupBy(r => r.SensorId, StringComparer.OrdinalIgnoreCase))
        {
            var sorted = group.OrderBy(r => r.Timestamp).ToList();
            string canonicalId = canonicalNames[group.Key];

            for (int i = 0; i < sorted.Count; i++)
            {
                var cur = sorted[i];
                var (min, max) = cur.StorageClass == StorageClass.Cold ? (2.0m, 8.0m) : (-22.0m, -15.0m);
                bool outside = cur.Temperature < min || cur.Temperature > max;
                bool abrupt = i > 0 && Math.Abs(cur.Temperature - sorted[i - 1].Temperature) > 4.0m;

                if (outside || abrupt)
                {
                    var reasons = new List<string>();
                    if (outside) reasons.Add("Outside range");
                    if (abrupt) reasons.Add("Abrupt change");
                    alerts.Add(new Alert(canonicalId, cur.Timestamp, cur.Temperature, reasons.AsReadOnly()));
                }
            }
        }

        return alerts
            .OrderBy(a => a.SensorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Timestamp)
            .ToList()
            .AsReadOnly();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() 
    { 
        WriteIndented = true, 
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, 
        Converters = { new JsonStringEnumConverter() } 
    };

    public static void WriteArchive(Stream output, MonitoringArchive archive) => JsonSerializer.Serialize(output, archive, JsonOpts);

    public static MonitoringArchive ReadArchive(Stream input) => JsonSerializer.Deserialize<MonitoringArchive>(input, JsonOpts) ?? throw new InvalidOperationException("Deserialized archive cannot be null.");
}

public class Program
{
    public static void Main()
    {
        string inputPath = "readings.txt";
        string archivePath = "archive.json";

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Error: File '{inputPath}' not found.");
            return;
        }

        ImportResult result;
        using (var fs = File.OpenRead(inputPath))
        {
            result = ColdChainLogic.ReadReadings(fs);
        }

        var archive = new MonitoringArchive(
            DateTimeOffset.UtcNow,
            result.Readings,
            result.Errors,
            ColdChainLogic.Analyze(result.Readings)
        );

        using (var fs = File.Create(archivePath))
        {
            ColdChainLogic.WriteArchive(fs, archive);
        }

        MonitoringArchive restored;
        using (var fs = File.OpenRead(archivePath))
        {
            restored = ColdChainLogic.ReadArchive(fs);
        }

        Console.WriteLine($"=== Archive Processing Results ===");
        Console.WriteLine($"Valid Readings : {restored.Readings.Count}");
        Console.WriteLine($"Import Errors  : {restored.Errors.Count}");
        Console.WriteLine($"Alerts         : {restored.Alerts.Count}");
    }
}
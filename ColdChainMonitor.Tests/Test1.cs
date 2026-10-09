using System.Text;
using System.Text.Json;
using Xunit;
using ColdChainMonitor;

namespace ColdChainMonitor.Tests;

public class ColdChainTests
{
    private const string SampleData = 
        "S1|2026-09-22T08:10:00Z|Cold|5.2\n" +
        "S2|2026-09-22T08:01:00Z|Frozen|-18.0\n" +
        "S1|2026-09-22T08:00:00Z|Cold|4.0\n" +
        "S1|2026-09-22T08:05:00Z|Cold|8.7\n" +
        "S2|2026-09-22T08:06:00Z|Frozen|-12.0\n" +
        "S1|2026-09-22T08:15:00Z|Cold|8.0\n" +
        "S3|bad-date|Cold|3.5\n" +
        "S2|2026-09-22T08:11:00Z|99|-17.0\n" +
        "S1|2026-09-22T08:20:00Z|Cold|5,5\n" +
        "S1|2026-09-22T08:25:00Z|Frozen|-17.0";

    [Fact]
    public void Test01_SampleFile_ProducesExpectedCounts()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(SampleData));
        var res = ColdChainLogic.ReadReadings(ms);
        var alerts = ColdChainLogic.Analyze(res.Readings);

        Assert.Equal(6, res.Readings.Count);
        Assert.Equal(4, res.Errors.Count);
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void Test02_NumericEnum_Rejected()
    {
        Assert.False(ColdChainLogic.TryParseReading("S1|2026-09-22T08:00:00Z|99|5.0", out _, out _));
    }

    [Fact]
    public void Test03_CommaDecimal_Rejected()
    {
        Assert.False(ColdChainLogic.TryParseReading("S1|2026-09-22T08:00:00Z|Cold|5,5", out _, out _));
    }

    [Fact]
    public void Test04_ClassMismatch_CreatesImportError()
    {
        string data = "S1|2026-09-22T08:00:00Z|Cold|5.0\nS1|2026-09-22T08:05:00Z|Frozen|-18.0";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(data));
        var res = ColdChainLogic.ReadReadings(ms);

        Assert.Single(res.Readings);
        Assert.Single(res.Errors);
    }

    [Fact]
    public void Test05_SafeBoundaries()
    {
        var readings = new[]
        {
            new Reading("S1", DateTimeOffset.UtcNow, StorageClass.Cold, 8.0m),
            new Reading("S2", DateTimeOffset.UtcNow, StorageClass.Frozen, -15.0m)
        };
        Assert.Empty(ColdChainLogic.Analyze(readings));
    }

    [Fact]
    public void Test06_AdjacentComparisons_DoNotCrossSensors()
    {
        var readings = new[]
        {
            new Reading("S1", DateTimeOffset.UtcNow, StorageClass.Cold, 4.0m),
            new Reading("S2", DateTimeOffset.UtcNow, StorageClass.Cold, 12.0m)
        };
        var alerts = ColdChainLogic.Analyze(readings);
        Assert.Single(alerts);
        Assert.DoesNotContain(alerts[0].Reasons, r => r == "Abrupt change");
    }

    [Fact]
    public void Test07_InputOrder_DoesNotChangeAlerts()
    {
        var r1 = new Reading("S1", DateTimeOffset.Parse("2026-09-22T08:10:00Z"), StorageClass.Cold, 8.7m);
        var r2 = new Reading("S1", DateTimeOffset.Parse("2026-09-22T08:00:00Z"), StorageClass.Cold, 4.0m);

        var alerts = ColdChainLogic.Analyze(new[] { r1, r2 });
        Assert.Single(alerts);
        Assert.Equal(2, alerts[0].Reasons.Count);
    }

    [Fact]
    public void Test08_ReadReadings_LeavesStreamOpen()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(SampleData));
        ColdChainLogic.ReadReadings(ms);
        Assert.True(ms.CanRead);
    }

    [Fact]
    public void Test09_WriteArchive_LeavesStreamOpen()
    {
        using var ms = new MemoryStream();
        var archive = new MonitoringArchive(DateTimeOffset.UtcNow, Array.Empty<Reading>(), Array.Empty<ImportError>(), Array.Empty<Alert>());
        ColdChainLogic.WriteArchive(ms, archive);
        Assert.True(ms.CanWrite);
    }

    [Fact]
    public void Test10_RoundtripAndEnumNameSerialization()
    {
        using var ms = new MemoryStream();
        var archive = new MonitoringArchive(
            DateTimeOffset.UtcNow,
            new[] { new Reading("S1", DateTimeOffset.UtcNow, StorageClass.Cold, 5.0m) },
            Array.Empty<ImportError>(),
            Array.Empty<Alert>()
        );

        ColdChainLogic.WriteArchive(ms, archive);
        
        string json = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("\"Cold\"", json);
        
        ms.Position = 0;

        var restored = ColdChainLogic.ReadArchive(ms);
        Assert.Single(restored.Readings);
        Assert.Equal(archive.Readings[0].Temperature, restored.Readings[0].Temperature);
    }

    [Fact]
    public void Test11_MaterializedResults_WorkAfterStreamDisposed()
    {
        ImportResult res;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(SampleData)))
        {
            res = ColdChainLogic.ReadReadings(ms);
        }
        Assert.Equal(6, res.Readings.Count);
    }

    [Fact]
    public void Test12_ReadArchive_LeavesStreamOpen()
    {
        using var ms = new MemoryStream();
        var archive = new MonitoringArchive(DateTimeOffset.UtcNow, Array.Empty<Reading>(), Array.Empty<ImportError>(), Array.Empty<Alert>());
        ColdChainLogic.WriteArchive(ms, archive);
        ms.Position = 0;

        ColdChainLogic.ReadArchive(ms);
        Assert.True(ms.CanRead);
    }

    [Fact]
    public void Test13_ReadReadings_RespectsCurrentPosition()
    {
        string header = "HEADER_LINE\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(header + SampleData));
        ms.Position = Encoding.UTF8.GetByteCount(header);

        var res = ColdChainLogic.ReadReadings(ms);
        Assert.Equal(6, res.Readings.Count);
    }

    [Fact]
    public void Test14_MalformedJson_ThrowsJsonException()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{ invalid_json }"));
        Assert.Throws<JsonException>(() => ColdChainLogic.ReadArchive(ms));
    }
}
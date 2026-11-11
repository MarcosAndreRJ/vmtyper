using System.IO;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Services.Persistence;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class PersistenceTests : IDisposable
{
    private const string Corrupt = "[{\"Id\":\"5b1c\", \"Text\":\"fila preparada importante\"  <-- truncado";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Queue_RoundTripsTextOrderAndStatus()
    {
        var path = _dir.File("queue.json");
        var store = new QueuePersistenceService(path);
        var blocks = new List<TextBlockItem>
        {
            new() { Order = 1, Text = "um", Status = TextBlockStatus.Sent },
            new() { Order = 2, Text = "dois", Status = TextBlockStatus.Ready },
        };

        store.Save(blocks);
        var loaded = new QueuePersistenceService(path).Load();

        Assert.Equal(new[] { "um", "dois" }, loaded.Select(b => b.Text));
        Assert.Equal(new[] { TextBlockStatus.Sent, TextBlockStatus.Ready }, loaded.Select(b => b.Status));
        Assert.Equal(blocks.Select(b => b.Id), loaded.Select(b => b.Id));
    }

    [Fact]
    public void Settings_RoundTripsSubstitutions()
    {
        var path = _dir.File("settings.json");
        var settings = new AppSettings { CharDelayMs = 7 };
        settings.CharacterSubstitutions.Add(new CharacterSubstitution { From = "/", To = "|" });

        new SettingsService(path).Save(settings);
        var loaded = new SettingsService(path).Load();

        Assert.Equal(7, loaded.CharDelayMs);
        Assert.Equal("|", Assert.Single(loaded.CharacterSubstitutions).To);
    }

    [Fact]
    [Trait("Known", "F")]
    public void CorruptQueueFile_IsNotOverwritten()
    {
        var path = _dir.File("queue.json");
        File.WriteAllText(path, Corrupt);

        var queue = new TypingQueueService(TestLog.Create(_dir), new QueuePersistenceService(path), new ManualAutoSaveScheduler());
        queue.AddBlock(); // qualquer ação que grave a fila

        Assert.Contains(Corrupt, _dir.AllContent());
    }

    [Fact]
    [Trait("Known", "F")]
    public void CorruptSettingsFile_IsReported()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, Corrupt);

        var service = new SettingsService(path);
        var result = service.LoadWithResult();
        Assert.Equal(LoadStatus.Corrupt, result.Status);
        Assert.NotNull(result.PreservedCorruptPath);
        Assert.True(File.Exists(result.PreservedCorruptPath));

        service.Save(new AppSettings());

        Assert.Contains(Corrupt, _dir.AllContent());
    }

    [Fact]
    public void CorruptQueueFile_IsPreservedAndReported()
    {
        var path = _dir.File("queue.json");
        File.WriteAllText(path, Corrupt);

        var store = new QueuePersistenceService(path);
        var result = store.LoadWithResult();

        Assert.Equal(LoadStatus.Corrupt, result.Status);
        Assert.Null(result.Value);
        Assert.NotNull(result.PreservedCorruptPath);
        Assert.True(File.Exists(result.PreservedCorruptPath));
        Assert.Equal(Corrupt, File.ReadAllText(result.PreservedCorruptPath!));
    }

    [Fact]
    public void TruncatedTmp_DoesNotAffectMain()
    {
        var path = _dir.File("queue.json");
        var store = new QueuePersistenceService(path);
        var blocks = new List<TextBlockItem>
        {
            new() { Order = 1, Text = "bloco íntegro", Status = TextBlockStatus.Ready }
        };
        store.Save(blocks);

        // Simula uma gravação interrompida em queue.json.tmp.
        File.WriteAllText(path + ".tmp", "{\"schemaVersion\":2, \"truncado");

        var loadedResult = new QueuePersistenceService(path).LoadWithResult();
        Assert.Equal(LoadStatus.Ok, loadedResult.Status);
        Assert.Equal("bloco íntegro", Assert.Single(loadedResult.Value!).Text);
    }

    [Fact]
    public void BackupIsUsedWhenMainCorrupt()
    {
        var path = _dir.File("queue.json");
        var store = new QueuePersistenceService(path);
        store.Save(new List<TextBlockItem>
        {
            new() { Order = 1, Text = "versão 1 backup", Status = TextBlockStatus.Ready }
        });
        // Segunda gravação move a primeira para queue.json.bak via File.Replace.
        store.Save(new List<TextBlockItem>
        {
            new() { Order = 1, Text = "versão 2 atual", Status = TextBlockStatus.Ready }
        });

        Assert.True(File.Exists(path + ".bak"));

        // Corrompe o arquivo principal.
        File.WriteAllText(path, Corrupt);

        var loaded = new QueuePersistenceService(path).LoadWithResult();
        Assert.Equal(LoadStatus.RestoredFromBackup, loaded.Status);
        Assert.Equal("versão 1 backup", Assert.Single(loaded.Value!).Text);
        // O arquivo corrompido também foi preservado em .corrupt-*.
        Assert.NotNull(loaded.PreservedCorruptPath);
        Assert.True(File.Exists(loaded.PreservedCorruptPath!));
    }

    [Fact]
    public void V1Array_IsMigrated()
    {
        var path = _dir.File("queue.json");
        string v1 = """
        [
          { "Id": "11111111-1111-1111-1111-111111111111", "Order": 1, "Text": "texto v1", "Status": "Current" },
          { "Id": "22222222-2222-2222-2222-222222222222", "Order": 2, "Text": "", "Status": "Pending" },
          { "Id": "33333333-3333-3333-3333-333333333333", "Order": 3, "Text": "feito", "Status": "Completed" }
        ]
        """;
        File.WriteAllText(path, v1);

        var store = new QueuePersistenceService(path);
        var res = store.LoadWithResult();
        Assert.Equal(LoadStatus.Ok, res.Status);
        Assert.Equal(new[] { TextBlockStatus.Ready, TextBlockStatus.Empty, TextBlockStatus.Sent }, res.Value!.Select(b => b.Status));

        // Ao salvar novamente, grava com envelope schemaVersion 2.
        store.Save(res.Value!);
        string savedJson = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 2", savedJson);
    }

    [Fact]
    public void EncryptedRoundTrip()
    {
        var path = _dir.File("queue.json");
        const string secret = "SegredoSuperConfidencial#2026";
        var store = new QueuePersistenceService(path, () => QueueTextPersistence.Encrypted);

        store.Save(new List<TextBlockItem>
        {
            new() { Order = 1, Text = secret, Status = TextBlockStatus.Ready }
        });

        string rawDisk = File.ReadAllText(path);
        Assert.DoesNotContain(secret, rawDisk);
        Assert.Contains("dpapi:", rawDisk);

        var loaded = new QueuePersistenceService(path, () => QueueTextPersistence.Encrypted).Load();
        Assert.Equal(secret, Assert.Single(loaded).Text);
    }

    [Fact]
    public void NonePersistence_DoesNotWriteText()
    {
        var path = _dir.File("queue.json");
        const string secret = "TextoNaoPersistidoEmDisco999";
        var store = new QueuePersistenceService(path, () => QueueTextPersistence.None);

        store.Save(new List<TextBlockItem>
        {
            new() { Order = 1, Text = secret, Status = TextBlockStatus.Ready }
        });

        string rawDisk = File.ReadAllText(path);
        Assert.DoesNotContain(secret, rawDisk);

        var loaded = new QueuePersistenceService(path, () => QueueTextPersistence.None).Load();
        var item = Assert.Single(loaded);
        Assert.Equal(string.Empty, item.Text);
        Assert.Equal(TextBlockStatus.Empty, item.Status);
        Assert.Equal("texto não persistido", item.PersistenceNote);
        Assert.Contains("texto não persistido", item.StatusLabel);
    }
}

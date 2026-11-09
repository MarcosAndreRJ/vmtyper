using System.IO;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;

namespace VmTyper.Tests;

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

    [KnownDefectFact("F")]
    [Trait("Known", "F")]
    public void CorruptQueueFile_IsNotOverwritten()
    {
        var path = _dir.File("queue.json");
        File.WriteAllText(path, Corrupt);

        var queue = new TypingQueueService(TestLog.Create(_dir), new QueuePersistenceService(path), new ManualAutoSaveScheduler());
        queue.AddBlock(); // qualquer ação que grave a fila

        Assert.Contains(Corrupt, _dir.AllContent());
    }

    /// <summary>
    /// O "reportar" exige uma API que ainda não existe (F1). O que dá para
    /// verificar agora é a consequência: depois de ler um settings.json
    /// corrompido e salvar (o que a SettingsWindow faz), o conteúdo original
    /// não pode ter sumido sem rastro.
    /// </summary>
    [KnownDefectFact("F")]
    [Trait("Known", "F")]
    public void CorruptSettingsFile_IsReported()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, Corrupt);

        var service = new SettingsService(path);
        var loaded = service.Load();
        service.Save(loaded);

        Assert.Contains(Corrupt, _dir.AllContent());
    }
}

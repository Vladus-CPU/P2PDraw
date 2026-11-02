using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using P2PDraw.Core.Sync;

namespace P2PDraw.Core.Storage;

public sealed record ProjectFile(int FormatVersion, string Name, DateTimeOffset SavedAt, Snapshot Snapshot)
{
    public const int CurrentVersion = 1;
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static ProjectFile Create(string name, Snapshot snapshot)
    {
        var file = new ProjectFile(CurrentVersion, name, DateTimeOffset.UtcNow, snapshot);
        file.Validate();
        return file;
    }

    public string Serialize()
    {
        Validate();
        string json = JsonSerializer.Serialize(this, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
        {
            throw new InvalidDataException("Файл проєкту перевищує допустимий розмір 32 МіБ.");
        }
        return json;
    }

    public static ProjectFile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
        {
            throw new InvalidDataException("Файл проєкту перевищує допустимий розмір 32 МіБ.");
        }
        return Deserialize(Encoding.UTF8.GetBytes(json));
    }

    public static ProjectFile Deserialize(ReadOnlySpan<byte> contents)
    {
        if (contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Файл проєкту перевищує допустимий розмір 32 МіБ.");
        }
        if (contents.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            contents = contents[3..];
        }
        try
        {
            var file = JsonSerializer.Deserialize<ProjectFile>(contents, JsonOptions);
            if (file is null)
            {
                throw new InvalidDataException("Файл проєкту порожній.");
            }
            file.Validate();
            return file;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Файл проєкту містить некоректний JSON або невідомі дані.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Стан документа не відповідає журналу операцій або містить некоректні дані.", exception);
        }
    }

    public static async Task<ProjectFile> ReadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        using var contents = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (contents.Length + count > MaximumFileBytes)
            {
                throw new InvalidDataException("Файл проєкту перевищує допустимий розмір 32 МіБ.");
            }
            contents.Write(buffer, 0, count);
        }
        byte[] bytes = contents.ToArray();
        var file = await Task.Run(() => Deserialize(bytes), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return file;
    }

    private void Validate()
    {
        if (FormatVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Версія формату проєкту {FormatVersion} не підтримується. Очікується {CurrentVersion}.");
        }
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || SavedAt == default || SavedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Некоректна назва або дата збереження проєкту.");
        }
        if (Snapshot is null)
        {
            throw new InvalidDataException("Файл не містить стану документа.");
        }
        Snapshot.Validate();
    }
}

using System.Text.Json;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>
/// Display names asked for at join-token time for nodes that have no record yet (MN-102; W7 review MN-126): enrollment
/// keeps an existing record's address, so no placeholder record can be written before the agent enrolls, and the name is
/// applied at approval. Kept in <c>{DataPath}/pending-node-names.json</c> (a flat <c>{nodeId: name}</c> object, written
/// temp + flush + rename) so a master restart between the token and the approval does not lose it; without a path it is
/// memory only (tests). Not part of a backup (a pending name is operator convenience, not fleet state); an unreadable file
/// reads as empty and is replaced by the next write.
/// </summary>
public sealed class PendingNodeNames
{
    /// <summary>File name under the data path.</summary>
    public const string FileName = "pending-node-names.json";

    private readonly string? _path;
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _names;

    /// <summary>Creates the store over <paramref name="path"/>; null = memory only.</summary>
    public PendingNodeNames(string? path) => _path = path;

    /// <summary>The pending name of <paramref name="nodeId"/>, or null.</summary>
    public string? Get(string nodeId)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(nodeId);
        }
    }

    /// <summary>Remembers <paramref name="name"/> for <paramref name="nodeId"/>.</summary>
    public void Set(string nodeId, string name)
    {
        lock (_gate)
        {
            var names = Load();
            if (names.TryGetValue(nodeId, out string? current) && current == name)
            {
                return;
            }

            Save(new Dictionary<string, string>(names, StringComparer.Ordinal) { [nodeId] = name });
        }
    }

    /// <summary>Forgets the pending name of <paramref name="nodeId"/> (approved, rejected, revoked or its tokens expired).</summary>
    public void Remove(string nodeId)
    {
        lock (_gate)
        {
            var names = Load();
            if (!names.ContainsKey(nodeId))
            {
                return;
            }

            var updated = new Dictionary<string, string>(names, StringComparer.Ordinal);
            updated.Remove(nodeId);
            Save(updated);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_names is not null)
        {
            return _names;
        }

        _names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_path is not null && File.Exists(_path))
        {
            try
            {
                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_path));
                foreach (var (id, name) in read ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        _names[id] = name;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Convenience data: an unreadable file means "no pending names"; the next write replaces it.
            }
        }

        return _names;
    }

    private void Save(Dictionary<string, string> names)
    {
        if (_path is not null)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, names, MasterStateJson.Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
            DirectorySync.TryFlushParent(_path);
        }

        _names = names; // only after the rename: a failed write changes nothing
    }
}

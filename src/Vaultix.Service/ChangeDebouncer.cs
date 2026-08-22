using System.Collections.Concurrent;

namespace Vaultix.Service;

public sealed class ChangeDebouncer
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _changes = new();

    public void Mark(Guid folderId, DateTimeOffset timestampUtc) => _changes[folderId] = timestampUtc;

    public IReadOnlyCollection<Guid> DequeueDue(DateTimeOffset nowUtc, TimeSpan delay)
    {
        var due = new List<Guid>();
        foreach (var change in _changes.ToArray())
        {
            if (nowUtc - change.Value < delay) continue;
            if (((ICollection<KeyValuePair<Guid, DateTimeOffset>>)_changes).Remove(change)) due.Add(change.Key);
        }
        return due;
    }
}

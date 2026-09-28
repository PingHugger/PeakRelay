using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakRelay.Server;

/// <summary>
/// M0 room: membership set shared by sessions; fan-out itself is driven by each Session's
/// reader loop. Photon-level semantics (properties, actor numbers) arrive in M1.
/// </summary>
public sealed class Room
{
    private readonly object _sync = new();
    private readonly List<Session> _members = new();

    public Room(string name) => Name = name;

    public string Name { get; }

    public int MemberCount
    {
        get { lock (_sync) return _members.Count; }
    }

    public void Add(Session session)
    {
        lock (_sync)
        {
            if (!_members.Contains(session))
                _members.Add(session);
        }
    }

    public void Remove(Session session)
    {
        lock (_sync)
        {
            _members.Remove(session);
        }
    }

    public IReadOnlyList<Session> MembersExcluding(Session session)
    {
        lock (_sync)
        {
            return _members.Where(m => !ReferenceEquals(m, session)).ToList();
        }
    }
}

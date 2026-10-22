namespace Meridian.Core.Domain;

public sealed record MembershipPeriod(DateOnly From, DateOnly? To)
{
    public bool Contains(DateOnly date) => date >= From && (To is null || date <= To);
}

/// <summary>Member states and their membership periods. Identity is the ISO code, not the name (ING-11).</summary>
public sealed class MemberRegistry
{
    private readonly Dictionary<string, List<MembershipPeriod>> _periods;
    private readonly Dictionary<string, string> _names;

    public MemberRegistry(IEnumerable<(string Code, string Name, DateOnly From, DateOnly? To)> rows)
    {
        _periods = new(StringComparer.Ordinal);
        _names = new(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!_periods.TryGetValue(r.Code, out var list)) _periods[r.Code] = list = new();
            list.Add(new MembershipPeriod(r.From, r.To));
            _names[r.Code] = r.Name;
        }
    }

    public IReadOnlyCollection<string> Codes => _names.Keys;

    public bool Exists(string code) => _periods.ContainsKey(code);

    public string? NameOf(string code) => _names.GetValueOrDefault(code);

    public bool IsMemberOn(string code, DateOnly date) =>
        _periods.TryGetValue(code, out var list) && list.Any(p => p.Contains(date));
}

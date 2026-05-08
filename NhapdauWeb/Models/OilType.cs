namespace NhapdauWeb.Models;

public class OilType
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName => $"{Code} - {Name}";
}

public static class OilTypes
{
    public static readonly IReadOnlyList<OilType> All = new List<OilType>
    {
        new() { Code = "68010", Name = "P150A" },
        new() { Code = "68020", Name = "H-1" },
        new() { Code = "68041", Name = "TDAE OIL" },
        new() { Code = "68046", Name = "SB OIL" }
    };

    public static OilType? FindByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        return All.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsValidCode(string? code) => FindByCode(code) is not null;

    public static string? GetName(string? code) => FindByCode(code)?.Name;

    public static string GetDisplayName(string? code) =>
        FindByCode(code)?.DisplayName ?? code ?? string.Empty;
}

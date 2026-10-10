using System.Text.RegularExpressions;
using DatabaseMonitoring.Web.Models;

namespace DatabaseMonitoring.Web.Providers;

/// <summary>
/// MSSQL ve PostgreSQL sağlayıcıları arasında paylaşılan sorgu metni yardımcıları.
/// Tek fark tanımlayıcı tırnaklama biçimi ([] vs "") ve varsayılan şema adıdır.
/// </summary>
public static class SqlTextHelper
{
    public static string TrimQuery(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    /// <summary>
    /// "FROM/JOIN [şema].[tablo]" veya "FROM/JOIN "şema"."tablo"" kalıplarını tanır.
    /// Şema belirtilmemişse defaultSchema kullanılır.
    /// </summary>
    public static string? ExtractPrimaryTable(string sql, string defaultSchema)
    {
        var patterns = new[]
        {
            @"(?:FROM|JOIN)\s+[\[""]?(\w+)[\]""]?\.[\[""]?(\w+)[\]""]?",
            @"(?:FROM|JOIN)\s+[\[""]?(\w+)[\]""]?(?!\s*\.)"
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(sql, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups.Count >= 3 && m.Groups[2].Success
                    ? $"{m.Groups[1].Value}.{m.Groups[2].Value}"
                    : $"{defaultSchema}.{m.Groups[1].Value}";
            }
        }
        return null;
    }

    /// <summary>
    /// Ham (LastExecutionTime, QueryText) satırlarından, tablo adına göre en güncel sorguyu eşler.
    /// </summary>
    public static Dictionary<string, QueryInfo> BuildLastQueryPerTable(IEnumerable<QueryInfo> rows, string defaultSchema)
    {
        var byTable = new Dictionary<string, QueryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.QueryText)) continue;
            var key = ExtractPrimaryTable(row.QueryText, defaultSchema);
            if (key != null && !byTable.ContainsKey(key))
                byTable[key] = row;
        }
        return byTable;
    }
}

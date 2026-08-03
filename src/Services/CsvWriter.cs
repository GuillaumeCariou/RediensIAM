namespace RediensIAM.Services;

/// <summary>
/// CSV cell escaping for the user and audit-log exports.
///
/// Beyond the usual quoting, this neutralises spreadsheet formula injection: a cell whose first
/// character is <c>= + - @</c> (or TAB / CR) is evaluated as a formula by Excel, LibreOffice and
/// Google Sheets. The payload is end-user controlled — anyone can set their own display name via
/// <c>PATCH /account/me</c> — and the export is opened on an administrator's machine.
/// </summary>
public static class CsvWriter
{
    private static readonly char[] FormulaLeadCharacters = ['=', '+', '-', '@', '\t', '\r'];

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Prefix with an apostrophe so spreadsheets treat the cell as literal text.
        // OWASP's recommended mitigation; the leading quote is stripped on display.
        var cell = FormulaLeadCharacters.Contains(value[0]) ? "'" + value : value;

        if (cell.Contains(',') || cell.Contains('"') || cell.Contains('\n') || cell.Contains('\r'))
            return $"\"{cell.Replace("\"", "\"\"")}\"";
        return cell;
    }
}

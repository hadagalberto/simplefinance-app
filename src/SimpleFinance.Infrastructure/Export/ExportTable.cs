namespace SimpleFinance.Infrastructure.Export;

/// <summary>
/// RPT-02: the single report-agnostic shape every CSV/XLSX writer in <see cref="ExportService"/>
/// consumes -- every cell is already a formatted string (invariant-culture decimal formatting,
/// <c>yyyy-MM-dd</c> dates) by the time it reaches this record. Neither <see
/// cref="ExportService.WriteCsv"/> nor <see cref="ExportService.WriteXlsx"/> ever re-formats a
/// value; they only write what they're given. This is what lets both writers stay generic --
/// zero report-specific export logic needs writing per report type.
/// </summary>
public sealed record ExportTable(string[] Headers, IReadOnlyList<string[]> Rows);

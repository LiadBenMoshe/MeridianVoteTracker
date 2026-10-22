using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Core.Pipeline;
using Meridian.Core.Extraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure.Extraction;

public sealed class DocumentIntelligenceOptions
{
    public string? Endpoint { get; set; }
    public string? Key { get; set; }
    public string ModelId { get; set; } = "prebuilt-layout";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Backoff between attempts = RetryDelaySeconds × attempt number.</summary>
    public int RetryDelaySeconds { get; set; } = 15;
}

public interface ILayoutReader
{
    bool IsConfigured { get; }
    Task<LayoutDocument> ReadAsync(Stream pdf, CancellationToken ct);
}

/// <summary>Sends our correlation id with every Azure AI call so a request can be followed end to end.</summary>
internal sealed class CorrelationPolicy : HttpPipelineSynchronousPolicy
{
    public override void OnSendingRequest(HttpMessage message)
    {
        if (Correlation.Id is { } id) message.Request.Headers.SetValue(Correlation.HeaderName, id);
    }
}

/// <summary>Azure AI Document Intelligence (prebuilt-layout): OCR + table structure + word confidence.</summary>
/// <summary>
/// Auth: the key if DocumentIntelligence:Key is set, otherwise the app's Entra identity
/// (needs the "Cognitive Services User" role on the resource).
/// </summary>
public sealed class DocumentIntelligenceReader(IOptions<DocumentIntelligenceOptions> options, TokenCredential credential, ILogger<DocumentIntelligenceReader> log) : ILayoutReader
{
    private readonly DocumentIntelligenceOptions _o = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.Endpoint);

    public async Task<LayoutDocument> ReadAsync(Stream pdf, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Azure Document Intelligence is not configured (DocumentIntelligence__Endpoint).");

        var clientOptions = new DocumentIntelligenceClientOptions();
        clientOptions.AddPolicy(new CorrelationPolicy(), HttpPipelinePosition.PerCall);
        clientOptions.Retry.MaxRetries = 2;
        var client = string.IsNullOrWhiteSpace(_o.Key)
            ? new DocumentIntelligenceClient(new Uri(_o.Endpoint!), credential, clientOptions)
            : new DocumentIntelligenceClient(new Uri(_o.Endpoint!), new AzureKeyCredential(_o.Key), clientOptions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));

        log.LogInformation("Calling Document Intelligence model {Model}", _o.ModelId);
        var data = await BinaryData.FromStreamAsync(pdf, timeout.Token);
        var op = await client.AnalyzeDocumentAsync(WaitUntil.Completed, _o.ModelId, data, timeout.Token);
        var result = op.Value;
        log.LogInformation("Document Intelligence returned {Pages} page(s), {Tables} table(s)", result.Pages.Count, result.Tables.Count);
        return ToLayout(result);
    }

    private static LayoutDocument ToLayout(AnalyzeResult result)
    {
        var words = result.Pages.SelectMany(p => p.Words).ToList();

        double ConfidenceOf(IReadOnlyList<DocumentSpan> spans)
        {
            var c = words.Where(w => spans.Any(s => w.Span.Offset >= s.Offset && w.Span.Offset < s.Offset + s.Length))
                .Select(w => (double)w.Confidence).ToList();
            return c.Count == 0 ? 1.0 : c.Min();
        }

        var pageTexts = result.Pages
            .Select(p => string.Join("\n", p.Lines.Select(l => l.Content)))
            .ToList();

        var tables = result.Tables.Select(t =>
        {
            var rows = new List<LayoutCell>[t.RowCount];
            for (var r = 0; r < t.RowCount; r++)
                rows[r] = Enumerable.Range(0, t.ColumnCount).Select(_ => new LayoutCell("", 1.0)).ToList();
            foreach (var cell in t.Cells)
                rows[cell.RowIndex][cell.ColumnIndex] = new LayoutCell(cell.Content ?? "", ConfidenceOf(cell.Spans));
            var page = t.BoundingRegions.Count > 0 ? t.BoundingRegions[0].PageNumber : 1;
            return new LayoutTable(page, rows.Select(r => (IReadOnlyList<LayoutCell>)r).ToList());
        }).ToList();

        return new LayoutDocument(pageTexts, tables);
    }
}

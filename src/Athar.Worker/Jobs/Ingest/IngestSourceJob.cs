using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Sources;
using Athar.Worker.Jobs.Upload;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Athar.Worker.Jobs.Ingest;

/// <summary>
/// <c>ingest-source</c> (FR-KS-06, T142): snapshots a URL source (one page, SSRF-guarded) or reads an uploaded document,
/// extracts its text (no OCR), and indexes it like a GitHub doc: project-scoped chunks with the source's classification,
/// so the project's members are the audience. A refresh replaces the snapshot; unchanged text keeps its chunks.
/// </summary>
public sealed class IngestSourceJob(AttachmentStore store, IConfiguration config, TimeProvider clock, ILogger<IngestSourceJob> log) : IJob
{
    public string Kind => IngestRules.IngestKind;

    public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<IngestRules.IngestPayload>(job.Payload, IndexJobs.Json)!;
        var source = await db.KnowledgeSources.SingleOrDefaultAsync(s => s.Id == p.SourceId, ct);
        if (source is null || source.Kind is not (SourceKind.Url or SourceKind.Document)) return;
        var doc = await db.SourceDocuments.FirstOrDefaultAsync(d => d.SourceId == source.Id, ct);

        string? title, text, media;
        byte[] bytes;
        try
        {
            if (source.Kind == SourceKind.Url)
                (bytes, media) = await FetchAsync(new Uri(source.Repo), ct);
            else
            {
                if (doc?.StoredFile is not { } key) return;
                (bytes, media) = (await File.ReadAllBytesAsync(store.PathOf(key), ct), doc.MediaType ?? IngestRules.MediaTypeOf(source.Repo));
            }
            if (media is null) throw new InvalidDataException("That isn't a supported document type.");
            string? error;
            ((title, text), error) = await ParseAsync(media, bytes);
            if (error is not null) throw new InvalidDataException(error);
        }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or TaskCanceledException or IOException)
        {
            // Not retried: the same page or file would fail the same way. The person sees the error and can Refresh.
            log.LogWarning("Source {Id} ingest failed: {Error}", source.Id, e.Message);
            source.Status = SourceStatus.Error;
            db.Audit(source.OrgId, null, "source.ingest_failed", "knowledge_source", source.Id, after: new { source.Repo, error = e.Message });
            return;
        }

        text = text!.Length > IngestRules.MaxTextChars ? text[..IngestRules.MaxTextChars] : text;
        title = string.IsNullOrWhiteSpace(title) ? source.Title ?? (source.Kind == SourceKind.Url ? new Uri(source.Repo).Host : source.Repo) : title.Trim();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        doc ??= db.SourceDocuments.Add(new SourceDocument { OrgId = source.OrgId, SourceId = source.Id, Path = source.Repo, ContentMd = "", ContentHash = "" }).Entity;
        (doc.MediaType, doc.SizeBytes, doc.FetchedAt, doc.TombstonedAt) = (media, bytes.LongLength, clock.GetUtcNow(), null);
        (source.Title, source.Status) = (title, SourceStatus.Ok);
        if (doc.ContentHash != hash)
        {
            (doc.Title, doc.ContentMd, doc.ContentHash) = (title, text, hash);
            var revision = clock.GetUtcNow().ToUnixTimeMilliseconds();
            await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.SourceDocument && c.OwnerId == doc.Id).ExecuteDeleteAsync(ct);
            db.Chunks.AddRange(Chunker.Build(ChunkOwner.SourceDocument, doc.Id, revision, title, text, c =>
            {
                (c.Scope, c.OwnerProjectId, c.IsConfidential) = (Scope.Project, source.ProjectId, source.IsConfidential);
            }));
            db.Jobs.Add(IndexJobs.Vectors(ChunkOwner.SourceDocument, doc.Id, revision, clock.GetUtcNow()));
        }
        db.Audit(source.OrgId, null, "source.ingest", "knowledge_source", source.Id, after: new { source.Repo, media, bytes = bytes.LongLength, changed = doc.ContentHash == hash });
    }

    /// <summary>Text by media type, on its own thread with the parse timeout (PdfPig and OpenXml can't be cancelled).</summary>
    public static async Task<((string? Title, string Text), string? Error)> ParseAsync(string media, byte[] bytes)
    {
        (string? Title, string Text) result = default;
        var (_, error) = await ExtractAttachmentJob.WithTimeoutAsync(() => { result = Parse(media, bytes); return ""; }, IngestRules.ParseTimeout);
        return (result, error);
    }

    public static (string? Title, string Text) Parse(string media, byte[] bytes) => media switch
    {
        "text/html" => IngestRules.HtmlToText(Utf8(bytes)),
        "text/markdown" or "text/plain" => (null, Utf8(bytes).Trim()),
        "application/pdf" => (null, ExtractAttachmentJob.Pdf(bytes)),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => (null, ExtractAttachmentJob.Docx(bytes)),
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => (null, Pptx(bytes)),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => (null, Xlsx(bytes)),
        _ => throw new InvalidDataException($"No text in {media}."),
    };

    private static string Utf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

    /// <summary>Each slide's text, in order, as a section.</summary>
    private static string Pptx(byte[] bytes)
    {
        using var deck = PresentationDocument.Open(new MemoryStream(bytes, writable: false), isEditable: false);
        var text = new StringBuilder();
        var n = 0;
        foreach (var id in deck.PresentationPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [])
        {
            if (deck.PresentationPart!.GetPartById(id.RelationshipId!) is not SlidePart slide) continue;
            text.AppendLine($"## Slide {++n}");
            foreach (var p in slide.Slide?.Descendants<A.Paragraph>() ?? []) if (p.InnerText is { Length: > 0 } t) text.AppendLine(t);
            text.AppendLine();
            if (text.Length > IngestRules.MaxTextChars) break;
        }
        return text.ToString().Trim();
    }

    /// <summary>Each sheet as rows of cell values (shared strings resolved), tab-separated.</summary>
    private static string Xlsx(byte[] bytes)
    {
        using var book = SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), isEditable: false);
        var wb = book.WorkbookPart ?? throw new InvalidDataException("No workbook.");
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        var text = new StringBuilder();
        foreach (var sheet in wb.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            if (wb.GetPartById(sheet.Id!) is not WorksheetPart part) continue;
            text.AppendLine($"## {sheet.Name}");
            foreach (var row in part.Worksheet?.Descendants<S.Row>() ?? [])
            {
                var cells = row.Elements<S.Cell>().Select(c => c.DataType?.Value == S.CellValues.SharedString && int.TryParse(c.CellValue?.Text, out var i) && i < shared.Length
                    ? shared[i] : c.DataType?.Value == S.CellValues.InlineString ? c.InnerText : c.CellValue?.Text ?? "");
                if (string.Join('\t', cells).TrimEnd() is { Length: > 0 } line) text.AppendLine(line);
                if (text.Length > IngestRules.MaxTextChars) break;
            }
            text.AppendLine();
        }
        return text.ToString().Trim();
    }

    /// <summary>
    /// One page, SSRF-guarded: every hop's host is resolved here and the connection goes to an address that passed
    /// <see cref="IngestRules.IsPublic"/> (so DNS can't rebind it), redirects are followed by hand and re-checked, and the body
    /// is capped at 10 MB within the timeout. <c>Athar:Sources:TrustedHosts</c> exempts named hosts (tests only).
    /// </summary>
    private async Task<(byte[] Bytes, string? Media)> FetchAsync(Uri url, CancellationToken ct)
    {
        var trusted = config.GetSection("Athar:Sources:TrustedHosts").Get<string[]>() ?? [];
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectCallback = async (ctx, token) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, token);
                var ok = trusted.Contains(host, StringComparer.OrdinalIgnoreCase) ? addresses.FirstOrDefault() : addresses.FirstOrDefault(IngestRules.IsPublic);
                if (ok is null || !trusted.Contains(host, StringComparer.OrdinalIgnoreCase) && addresses.Any(a => !IngestRules.IsPublic(a)))
                    throw new HttpRequestException($"{host} isn't a public address.");
                var socket = new Socket(ok.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        };
        using var http = new HttpClient(handler) { Timeout = IngestRules.FetchTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Athar/1.0 (+source snapshot)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,text/plain,text/markdown,application/pdf;q=0.9,*/*;q=0.5");

        for (var hop = 0; ; hop++)
        {
            if (url.Scheme is not ("http" or "https")) throw new InvalidDataException("Only http and https pages are read.");
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } next)
            {
                if (hop >= IngestRules.MaxRedirects) throw new InvalidDataException("Too many redirects.");
                url = new Uri(url, next);
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"The page answered {(int)res.StatusCode}.");
            if (res.Content.Headers.ContentLength > IngestRules.MaxBytes) throw new InvalidDataException("The page is larger than 10 MB.");
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            for (int read; (read = await body.ReadAsync(chunk, ct)) > 0;)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > IngestRules.MaxBytes) throw new InvalidDataException("The page is larger than 10 MB.");
            }
            var type = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var media = type is "text/html" or "text/plain" or "text/markdown" or "application/pdf" or "application/xhtml+xml"
                ? (type == "application/xhtml+xml" ? "text/html" : type)
                : IngestRules.MediaTypeOf(url.AbsolutePath);
            return (buffer.ToArray(), media);
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>
/// Download and upload details other package servers' issue trackers showed going wrong elsewhere, where FiGet's code was
/// judged right but nothing proved it (cross-check of 2026-09-15).
/// </summary>
public abstract partial class AssetDirectoryTests
{
    /// <summary>A resumed download reads where its bytes sit from Content-Range, not only the bytes themselves.</summary>
    [Fact]
    public async Task A_range_answer_says_which_bytes_of_how_many()
    {
        var path = $"endpoints/files/content/{Unique()}/data.bin";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync(path, new ByteArrayContent([0, 1, 2, 3, 4, 5, 6, 7, 8, 9])));

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(4, 6);
        using var response = await admin.SendAsync(request);

        HttpAssert.Status(HttpStatusCode.PartialContent, response);
        Assert.Equal("bytes 4-6/10", response.Content.Headers.ContentRange?.ToString());
    }

    /// <summary>A client that already holds the file asks with If-Modified-Since and gets 304 without the body.</summary>
    [Fact]
    public async Task An_unchanged_file_answers_not_modified_to_if_modified_since()
    {
        var path = $"endpoints/files/content/{Unique()}/script.ps1";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync(path, new ByteArrayContent("Write-Host 1"u8.ToArray())));

        using var first = await admin.GetAsync(path);
        var modified = first.Content.Headers.LastModified;
        Assert.NotNull(modified);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfModifiedSince = modified;
        using var again = await admin.SendAsync(request);
        HttpAssert.Status(HttpStatusCode.NotModified, again);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
    }

    /// <summary>An upload whose length is not known ahead - a chunked body, as a piped script sends - is stored whole.</summary>
    [Fact]
    public async Task A_chunked_upload_body_is_stored_whole()
    {
        var path = $"endpoints/files/content/{Unique()}/piped.bin";
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var content = new StreamContent(new UnknownLengthStream(bytes));
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        HttpAssert.Status(HttpStatusCode.Created, await admin.SendAsync(request));
        Assert.Equal(bytes, await admin.GetByteArrayAsync(path));
    }

    /// <summary>A readable stream that does not report its length, so HttpClient has to send it chunked.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 8192));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

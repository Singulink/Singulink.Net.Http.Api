namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class UploadSourceTests
{
    private static readonly byte[] Content = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];

    public TestContext TestContext { get; set; } = null!;

    private async Task<byte[]> ReadAsync(UploadSource source, long offset, int count)
    {
        byte[] buffer = new byte[count];
        await source.ReadExactlyAsync(offset, buffer, TestContext.CancellationToken);

        return buffer;
    }

    private static byte[] Range(int offset, int count) => Content.AsSpan(offset, count).ToArray();

    private static string CreateTempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"upload-source-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);

        return path;
    }

    [TestMethod]
    public void Constructor_RejectsEmptyFileNameAndNegativeLength()
    {
        Should.Throw<ArgumentException>(() => UploadSource.Create(string.Empty, 1, (_, _, _) => default));
        Should.Throw<ArgumentOutOfRangeException>(() => UploadSource.Create("a.bin", -1, (_, _, _) => default));
    }

    [TestMethod]
    public async Task FromFile_ReadsAnyRangeAndTakesItsNameFromThePath()
    {
        string path = CreateTempFile(Content);

        try
        {
            var source = UploadSource.FromFile(path, contentType: "application/octet-stream");

            source.FileName.ShouldBe(Path.GetFileName(path));
            source.Length.ShouldBe(100);
            source.ContentType.ShouldBe("application/octet-stream");

            (await ReadAsync(source, 50, 30)).ShouldBe(Range(50, 30));
            (await ReadAsync(source, 0, 10)).ShouldBe(Range(0, 10));
            UploadSource.FromFile(path, "report.pdf").FileName.ShouldBe("report.pdf");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FromFile_MissingFile_ThrowsFileNotFound()
    {
        Should.Throw<FileNotFoundException>(() => UploadSource.FromFile(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin")));
    }

    [TestMethod]
    public async Task FromFile_FileWhoseLengthChanged_FailsRead()
    {
        string path = CreateTempFile(Content);

        try
        {
            var source = UploadSource.FromFile(path);
            var lastWriteTime = File.GetLastWriteTimeUtc(path);

            File.WriteAllBytes(path, [.. Content, 1]);
            File.SetLastWriteTimeUtc(path, lastWriteTime);

            var ex = await Should.ThrowAsync<IOException>(() => ReadAsync(source, 0, 10));
            ex.Message.ShouldContain("has changed since the upload source was created");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task FromFile_FileWrittenWithTheSameLength_FailsRead()
    {
        string path = CreateTempFile(Content);

        try
        {
            var source = UploadSource.FromFile(path);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

            await Should.ThrowAsync<IOException>(() => ReadAsync(source, 0, 10));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FromStream_RequiresAReadableSeekableStream()
    {
        Should.Throw<ArgumentException>(() => UploadSource.FromStream(new ForwardOnlyStream(Content), "a.bin")).ParamName.ShouldBe("stream");
        Should.Throw<ArgumentException>(() => UploadSource.FromStream(new MemoryStream(Content, writable: false), string.Empty)).ParamName.ShouldBe("fileName");

        using var writeOnly = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
        Should.Throw<ArgumentException>(() => UploadSource.FromStream(writeOnly, "a.bin")).ParamName.ShouldBe("stream");
    }

    [TestMethod]
    public async Task FromStream_ReadsAnyRangeAndLeavesTheStreamOpen()
    {
        var stream = new MemoryStream(Content);
        var source = UploadSource.FromStream(stream, "a.bin", "image/png");

        source.Length.ShouldBe(100);
        source.ContentType.ShouldBe("image/png");
        (await ReadAsync(source, 90, 10)).ShouldBe(Range(90, 10));
        (await ReadAsync(source, 20, 5)).ShouldBe(Range(20, 5));

        await source.ReleaseAsync();
        stream.CanRead.ShouldBeTrue();
    }

    [TestMethod]
    public async Task FromStream_StreamWhoseLengthChanged_FailsRead()
    {
        var stream = new MemoryStream();
        stream.Write(Content, 0, Content.Length);
        var source = UploadSource.FromStream(stream, "a.bin");

        stream.SetLength(50);

        await Should.ThrowAsync<IOException>(() => ReadAsync(source, 0, 10));
    }

    [TestMethod]
    public async Task FromStreamFactory_ReadingForward_OpensOneStream()
    {
        var opened = new List<ForwardOnlyStream>();
        var source = CreateFactorySource(opened);

        for (int offset = 0; offset < 100; offset += 25)
            (await ReadAsync(source, offset, 25)).ShouldBe(Range(offset, 25));

        opened.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task FromStreamFactory_SkipsForwardInAStreamThatCantSeek()
    {
        var opened = new List<ForwardOnlyStream>();
        var source = CreateFactorySource(opened);

        (await ReadAsync(source, 0, 10)).ShouldBe(Range(0, 10));
        (await ReadAsync(source, 60, 20)).ShouldBe(Range(60, 20));

        opened.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task FromStreamFactory_GoingBackInAStreamThatCantSeek_OpensANewOne()
    {
        var opened = new List<ForwardOnlyStream>();
        var source = CreateFactorySource(opened);

        (await ReadAsync(source, 50, 25)).ShouldBe(Range(50, 25));
        (await ReadAsync(source, 25, 25)).ShouldBe(Range(25, 25));

        opened.Count.ShouldBe(2);
        opened[0].IsDisposed.ShouldBeTrue();
        opened[1].IsDisposed.ShouldBeFalse();
    }

    [TestMethod]
    public async Task FromStreamFactory_GoingBackInASeekableStream_Seeks()
    {
        int opens = 0;
        var source = UploadSource.FromStreamFactory("a.bin", 100, _ => {
            opens++;
            return Task.FromResult<Stream>(new MemoryStream(Content));
        });

        (await ReadAsync(source, 50, 25)).ShouldBe(Range(50, 25));
        (await ReadAsync(source, 0, 25)).ShouldBe(Range(0, 25));

        opens.ShouldBe(1);
    }

    [TestMethod]
    public async Task FromStreamFactory_Release_DisposesTheStreamAndTheNextReadOpensANewOne()
    {
        var opened = new List<ForwardOnlyStream>();
        var source = CreateFactorySource(opened);

        (await ReadAsync(source, 0, 50)).ShouldBe(Range(0, 50));
        await source.ReleaseAsync();

        opened.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();

        (await ReadAsync(source, 50, 50)).ShouldBe(Range(50, 50));
        opened.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task FromStreamFactory_AfterAFailedRead_OpensANewStream()
    {
        int opens = 0;
        var source = UploadSource.FromStreamFactory("a.bin", 100, _ => {
            Stream stream = ++opens is 1 ? new FailingStream() : new MemoryStream(Content);
            return Task.FromResult(stream);
        });

        await Should.ThrowAsync<IOException>(() => ReadAsync(source, 0, 10));
        (await ReadAsync(source, 0, 10)).ShouldBe(Range(0, 10));

        opens.ShouldBe(2);
    }

    [TestMethod]
    public async Task FromStreamFactory_SeekableStreamOfAnotherLength_FailsRead()
    {
        var source = UploadSource.FromStreamFactory("a.bin", 99, _ => Task.FromResult<Stream>(new MemoryStream(Content)));

        var ex = await Should.ThrowAsync<IOException>(() => ReadAsync(source, 0, 10));
        ex.Message.ShouldContain("has a length of 100 bytes");
    }

    [TestMethod]
    public async Task FromStreamFactory_StreamThatCantSeekAndIsLonger_FailsTheReadOfTheEnd()
    {
        var source = UploadSource.FromStreamFactory("a.bin", 99, _ => Task.FromResult<Stream>(new ForwardOnlyStream(Content)));

        (await ReadAsync(source, 0, 50)).ShouldBe(Range(0, 50));

        var ex = await Should.ThrowAsync<IOException>(() => ReadAsync(source, 50, 49));
        ex.Message.ShouldContain("longer than the content's length of 99 bytes");
    }

    [TestMethod]
    public async Task FromStreamFactory_StreamThatCantSeekAndIsShorter_FailsRead()
    {
        var source = UploadSource.FromStreamFactory("a.bin", 120, _ => Task.FromResult<Stream>(new ForwardOnlyStream(Content)));

        await Should.ThrowAsync<IOException>(() => ReadAsync(source, 110, 10));
        await Should.ThrowAsync<IOException>(() => ReadAsync(source, 90, 30));
    }

    [TestMethod]
    public async Task Create_CallsTheFunction()
    {
        var reads = new List<(long Offset, int Length)>();

        var source = UploadSource.Create("a.bin", 100, (offset, buffer, _) => {
            reads.Add((offset, buffer.Length));
            Range((int)offset, buffer.Length).CopyTo(buffer);

            return new ValueTask<int>(buffer.Length);
        }, "text/plain");

        source.ContentType.ShouldBe("text/plain");
        (await ReadAsync(source, 40, 20)).ShouldBe(Range(40, 20));
        reads.ShouldBe([(40, 20)]);
    }

    [TestMethod]
    public async Task ReadExactly_ReadsUntilTheBufferIsFull()
    {
        var source = UploadSource.Create("a.bin", 100, (offset, buffer, _) => {
            int count = Math.Min(buffer.Length, 3);
            Range((int)offset, count).CopyTo(buffer);

            return new ValueTask<int>(count);
        });

        (await ReadAsync(source, 10, 20)).ShouldBe(Range(10, 20));
    }

    [TestMethod]
    public async Task ReadExactly_ContentThatEndsEarly_ThrowsIOException()
    {
        var source = UploadSource.Create("a.bin", 100, (offset, buffer, _) => new ValueTask<int>(offset < 95 ? Math.Min(buffer.Length, 95 - (int)offset) : 0));

        var ex = await Should.ThrowAsync<IOException>(() => ReadAsync(source, 90, 10));
        ex.Message.ShouldBe("The content of the upload source ended at byte 95, before its length of 100 bytes.");
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(11)]
    public async Task ReadExactly_ReadThatReturnsAnInvalidCount_ThrowsInvalidOperation(int count)
    {
        var source = UploadSource.Create("a.bin", 100, (_, _, _) => new ValueTask<int>(count));

        await Should.ThrowAsync<InvalidOperationException>(() => ReadAsync(source, 0, 10));
    }

    private static UploadSource CreateFactorySource(List<ForwardOnlyStream> opened)
    {
        return UploadSource.FromStreamFactory("a.bin", 100, _ => {
            var stream = new ForwardOnlyStream(Content);
            opened.Add(stream);

            return Task.FromResult<Stream>(stream);
        });
    }

    private sealed class FailingStream() : MemoryStream(Content)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The file can't be read.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("The file can't be read.");

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("The file can't be read.");
#endif
    }
}

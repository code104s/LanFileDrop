using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanFileDrop.Core;
using LanFileDrop.Network;

namespace LanFileDrop.Tests;

public sealed class TransferTests
{
    [Fact]
    public void Progress_calculates_percentage_and_clamps()
    {
        var progress = new TransferProgress(Guid.NewGuid(), "sample.bin", 1000, 675, 10, TimeSpan.FromSeconds(1),
            TransferDirection.Send, TransferStatus.Running, "peer");
        Assert.Equal(67.5, progress.Percentage);
        Assert.Equal(100, (progress with { TransferredBytes = 2000 }).Percentage);
    }

    [Fact]
    public async Task History_round_trips_filters()
    {
        var root = NewTempDirectory();
        try
        {
            var repository = new HistoryRepository(Path.Combine(root, "history.db"));
            await repository.InitializeAsync();
            await repository.AddAsync(new TransferRecord(0, "one.bin", 2 * 1024 * 1024, "peer", "127.0.0.1",
                TransferDirection.Send, TransferStatus.Completed, "abcd", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

            var sent = await repository.QueryAsync(DateTimeOffset.UtcNow.AddMinutes(-1), TransferDirection.Send, 1024 * 1024);
            var received = await repository.QueryAsync(direction: TransferDirection.Receive);
            Assert.Single(sent);
            Assert.Equal("one.bin", sent[0].FileName);
            Assert.Empty(received);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Tcp_transfer_streams_multiple_chunks_and_verifies_sha256()
    {
        var root = NewTempDirectory();
        var sendDirectory = Path.Combine(root, "send");
        var receiveDirectory = Path.Combine(root, "receive");
        Directory.CreateDirectory(sendDirectory);
        Directory.CreateDirectory(receiveDirectory);
        var source = Path.Combine(sendDirectory, "payload.bin");
        var bytes = new byte[FileTransferService.ChunkSize * 2 + 12345];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        var repository = new HistoryRepository(Path.Combine(root, "history.db"));
        await repository.InitializeAsync();
        var port = FindFreePort();
        await using var receiver = new FileTransferService(repository);
        await using var sender = new FileTransferService(repository);
        receiver.IncomingTransfer += request => Task.FromResult(new TransferDecision(true, receiveDirectory));
        var received = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ProgressChanged += (_, progress) =>
        {
            if (progress.Status == TransferStatus.Completed) received.TrySetResult(progress);
        };
        receiver.Start(port);

        using var control = new TransferControl();
        var device = new DeviceInfo("local", "Loopback", IPAddress.Loopback, port, false, DateTimeOffset.UtcNow);
        await sender.SendPathAsync(device, source, control);
        var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(string.IsNullOrWhiteSpace(completed.Sha256));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(receiveDirectory, "payload.bin")));
        var history = await repository.QueryAsync();
        Assert.Contains(history, x => x.Direction == TransferDirection.Send && x.Status == TransferStatus.Completed);
        Assert.Contains(history, x => x.Direction == TransferDirection.Receive && x.Status == TransferStatus.Completed);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Tcp_sender_reconnects_and_resumes_from_receiver_offset()
    {
        var root = NewTempDirectory();
        try
        {
            var source = Path.Combine(root, "resume.bin");
            var payload = new byte[FileTransferService.ChunkSize * 3 + 777];
            new Random(91).NextBytes(payload);
            await File.WriteAllBytesAsync(source, payload);
            var repository = new HistoryRepository(Path.Combine(root, "history.db"));
            await repository.InitializeAsync();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var cutoff = FileTransferService.ChunkSize + 321;
            var server = Task.Run(async () =>
            {
                using (var first = await listener.AcceptTcpClientAsync())
                await using (var firstStream = first.GetStream())
                {
                    await ReadHeaderAsync(firstStream);
                    await WriteResumeResponseAsync(firstStream, 0);
                    var partial = new byte[cutoff];
                    await ReadExactlyAsync(firstStream, partial);
                    Assert.Equal(payload.AsSpan(0, cutoff).ToArray(), partial);
                }

                using var second = await listener.AcceptTcpClientAsync();
                await using var secondStream = second.GetStream();
                await ReadHeaderAsync(secondStream);
                await WriteResumeResponseAsync(secondStream, cutoff);
                var remainder = new byte[payload.Length - cutoff];
                await ReadExactlyAsync(secondStream, remainder);
                Assert.Equal(payload.AsSpan(cutoff).ToArray(), remainder);
                var hash = new byte[32];
                await ReadExactlyAsync(secondStream, hash);
                Assert.Equal(SHA256.HashData(payload), hash);
                await secondStream.WriteAsync(new byte[] { 1 });
            });

            await using var sender = new FileTransferService(repository);
            using var control = new TransferControl();
            var device = new DeviceInfo("resume", "Resume peer", IPAddress.Loopback, port, false, DateTimeOffset.UtcNow);
            await sender.SendPathAsync(device, source, control);
            await server.WaitAsync(TimeSpan.FromSeconds(15));
            listener.Stop();
            var history = await repository.QueryAsync();
            Assert.Contains(history, x => x.FileName == "resume.bin" && x.Status == TransferStatus.Completed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Web_share_requires_offer_then_streams_upload()
    {
        var root = NewTempDirectory();
        var storage = Path.Combine(root, "received");
        var repository = new HistoryRepository(Path.Combine(root, "history.db"));
        await repository.InitializeAsync();
        var port = FindFreePort();
        await using (var web = new WebShareService(repository, storage, port))
        {
            web.IncomingUpload += request => Task.FromResult(new TransferDecision(true, storage));
            var registered = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<DeviceInfo> latestDevices = Array.Empty<DeviceInfo>();
            web.WebClientsChanged += (_, devices) => { latestDevices = devices; registered.TrySetResult(devices); };
            await web.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var registrationResponse = await client.PostAsJsonAsync("/api/register",
                new { id = "iphone-test-1234", name = "iPhone của tôi" });
            registrationResponse.EnsureSuccessStatusCode();
            var devices = await registered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(devices, x => x.Name == "iPhone của tôi" && x.Kind == DeviceKind.WebBrowser);
            for (var i = 2; i <= 4; i++)
            {
                var more = await client.PostAsJsonAsync("/api/register",
                    new { id = $"mobile-device-{i}-1234", name = $"Thiết bị {i}" });
                more.EnsureSuccessStatusCode();
            }
            Assert.Equal(4, latestDevices.Count);
            var payload = new byte[FileTransferService.ChunkSize + 731];
            new Random(73).NextBytes(payload);
            var offerResponse = await client.PostAsJsonAsync("/api/offer",
                new { name = "mobile.bin", size = payload.LongLength, deviceName = "iPhone" });
            offerResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await offerResponse.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));

            using var content = new ByteArrayContent(payload);
            var uploadResponse = await client.PutAsync($"/api/upload/{token}", content);
            uploadResponse.EnsureSuccessStatusCode();
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(storage, "mobile.bin")));
            Assert.Empty(Directory.EnumerateFiles(storage, "*.partial"));

            var resumable = new byte[FileTransferService.ChunkSize * 2 + 97];
            new Random(74).NextBytes(resumable);
            var resumeOffer = await client.PostAsJsonAsync("/api/offer",
                new { name = "web-resume.bin", size = resumable.LongLength, deviceName = "iPhone" });
            resumeOffer.EnsureSuccessStatusCode();
            using var resumeJson = JsonDocument.Parse(await resumeOffer.Content.ReadAsStringAsync());
            var resumeToken = resumeJson.RootElement.GetProperty("token").GetString();
            using var firstBlock = new ByteArrayContent(resumable.AsSpan(0, FileTransferService.ChunkSize).ToArray());
            var interrupted = await client.PutAsync($"/api/upload/{resumeToken}?offset=0", firstBlock);
            Assert.False(interrupted.IsSuccessStatusCode);
            var statusJson = await client.GetStringAsync($"/api/upload/{resumeToken}/status");
            using var resumeStatus = JsonDocument.Parse(statusJson);
            var resumeOffset = resumeStatus.RootElement.GetProperty("offset").GetInt64();
            Assert.Equal(FileTransferService.ChunkSize, resumeOffset);
            using var remainder = new ByteArrayContent(resumable.AsSpan((int)resumeOffset).ToArray());
            var resumed = await client.PutAsync($"/api/upload/{resumeToken}?offset={resumeOffset}", remainder);
            resumed.EnsureSuccessStatusCode();
            Assert.Equal(resumable, await File.ReadAllBytesAsync(Path.Combine(storage, "web-resume.bin")));
            Assert.Empty(Directory.EnumerateFiles(storage, "*.partial"));

            const string message = "Xin chào từ iPhone — tiếng Việt UTF-8";
            var textPayload = Encoding.UTF8.GetBytes(message);
            var textOffer = await client.PostAsJsonAsync("/api/offer",
                new { name = "Văn bản-test.txt", size = textPayload.LongLength, deviceName = "iPhone",
                    clientId = "iphone-test-1234", isText = true });
            textOffer.EnsureSuccessStatusCode();
            using var textJson = JsonDocument.Parse(await textOffer.Content.ReadAsStringAsync());
            var textToken = textJson.RootElement.GetProperty("token").GetString();
            using var textContent = new ByteArrayContent(textPayload);
            var textUpload = await client.PutAsync($"/api/upload/{textToken}", textContent);
            textUpload.EnsureSuccessStatusCode();
            Assert.Equal(message, await File.ReadAllTextAsync(Path.Combine(storage, "Văn bản-test.txt"), Encoding.UTF8));
            var messageResponse = await client.GetStringAsync("/api/messages?clientId=iphone-test-1234");
            using var messagesJson = JsonDocument.Parse(messageResponse);
            Assert.Contains(messagesJson.RootElement.EnumerateArray(), x => x.GetProperty("content").GetString() == message);

            var iphone = devices.Single(x => x.Name == "iPhone của tôi");
            await web.PublishTextAsync(iphone, "Phản hồi trực tiếp từ PC");
            var updatedMessages = await client.GetStringAsync("/api/messages?clientId=iphone-test-1234");
            using var updatedJson = JsonDocument.Parse(updatedMessages);
            Assert.Contains(updatedJson.RootElement.EnumerateArray(),
                x => x.GetProperty("content").GetString() == "Phản hồi trực tiếp từ PC");
        }
        Directory.Delete(root, true);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LanFileDrop.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ReadHeaderAsync(Stream stream)
    {
        var size = new byte[4];
        await ReadExactlyAsync(stream, size);
        var header = new byte[BitConverter.ToInt32(size)];
        await ReadExactlyAsync(stream, header);
        using var json = JsonDocument.Parse(header);
        Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());
    }

    private static async Task WriteResumeResponseAsync(Stream stream, long offset)
    {
        var response = new byte[9];
        response[0] = 1;
        BitConverter.GetBytes(offset).CopyTo(response, 1);
        await stream.WriteAsync(response);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
